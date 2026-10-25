<!-- Adapted from stack-packs be-spring-boot@1.0.0 skills/be-spring-boot/SKILL.md, snapshot 2026-10-25 -->

# Java + Spring Boot

## Detect

Read `pom.xml` or `build.gradle(.kts)`, the Spring Boot and Java versions (toolchain), the package layering, Spring MVC vs WebFlux, the security setup, the test style and the formatter. **Follow the repo's Spring Boot major:** property names and defaults move between majors. Mirror the nearest feature.

## Scaffold

Write the project the Spring Initializr would produce, under the backend path:
- **`pom.xml`:** `spring-boot-starter-parent`, `java.version` pinned (an LTS), and the starters `web`, `validation`, `actuator`, `security`, plus the data pack's (`data-jpa`, `flyway`, `postgresql`) and test (`spring-boot-starter-test`, `spring-boot-testcontainers`, `testcontainers` `postgresql`). Add `springdoc-openapi-starter-webmvc-ui` (a version matching the Boot major), Spotless (`palantir-java-format` or `google-java-format`), and JaCoCo if coverage is wanted. Use current stable versions you are confident exist, and pin the Java version in `pom.xml` and `.java-version`.
- **Build tool:** Maven by default (Gradle if the repo uses it). If the repo has the Maven wrapper, use `./mvnw`. A new repo has no wrapper (its jar can't be written by hand), so CI and the Dockerfile use `mvn` from the image.
- **Sources:** `src/main/java/<group path>/<app>/Application.java` (`@SpringBootApplication`), `src/main/resources/application.yml` with profiles `default` (local), `test` and `prod`; `server.port` is the composition's dev port (e.g. 8081) locally and 8080 in the container.
- **`openapi/api.json`:** the initial contract (see Contract export).

## Conventions

- **Packages** by feature: `…app.<feature>` (controller, service, repository, dto, domain), plus `…app.common` (errors, security, config).
- **Domain rules** live in plain classes or entity methods, testable without Spring. Services orchestrate. Controllers stay thin.
- **DTOs** are `record`s with Bean Validation annotations, never entities. Map manually (or with MapStruct if the repo uses it).
- **Controllers:** `@RestController @RequestMapping("/api/<feature>")`, `@Valid` bodies, `ResponseEntity` with explicit statuses.
- **Injection:** constructor injection (final fields), no field `@Autowired`. Inject a `Clock` for time.
- **Logging:** SLF4J with placeholders (`log.info("Order {} locked", id)`), no PII.
- **Optional:** virtual threads (`spring.threads.virtual.enabled=true`) where the Java version supports them.

## Errors

Enable RFC 9457 with `spring.mvc.problemdetails.enabled=true`. A `@RestControllerAdvice` maps:
- domain exceptions to a `ProblemDetail` with `setProperty("errorCode", ErrorCodes.X)`
- `MethodArgumentNotValidException` to 400, with an `errors` map
- `OptimisticLockingFailureException` to 409 `CONFLICT`
- access denied to 403

The codes are constants in an `ErrorCodes` class mirroring the spec's error table.

## Auth

- **Spring Security:** a `SecurityFilterChain` bean.
  - **Same-origin with sessions:** form or JSON login with session cookies, and CSRF with the cookie token repository for the SPA.
  - **Cross-origin or external IdP:** OAuth2 resource server (JWT).
- **Authorisation:** method security (`@PreAuthorize`) plus ownership checks in services.
- **CORS:** only for the cross-origin model, with the origins read from `CORS_ALLOWED_ORIGINS`.
- The health endpoints are `permitAll`.

## Contract export

springdoc serves `/v3/api-docs`. Give every operation a stable ID with `@Operation(operationId = "getOrder")` (or an operation-ID customiser). The export is an `OpenApiExportIT` test (`@SpringBootTest(webEnvironment = RANDOM_PORT)`) that fetches `/v3/api-docs` and writes `openapi/api.json`, pretty-printed with stable ordering; CI runs it with `-Dtest=OpenApiExportIT`.

You can't run it, so **hand-write the change to `openapi/api.json` in springdoc's shape**: keep the existing file's `openapi` version, key order and indentation, and schema names equal to the DTO class names. Add each new path with its `operationId`, request body, responses (including the `ProblemDetail` error responses) and schemas. CI regenerates the file and fails on any difference.

## Config and database URL

- `SPRING_DATASOURCE_URL` (`jdbc:postgresql://host:5432/db`), `SPRING_DATASOURCE_USERNAME`, `SPRING_DATASOURCE_PASSWORD`.
- **Platform `DATABASE_URL`** (`postgres://user:pass@host:port/db`): an `EnvironmentPostProcessor` (registered in `META-INF/spring.factories` or the Boot major's equivalent) converts it before the DataSource is created, setting the three properties when only `DATABASE_URL` is present, with `?sslmode=require` for managed hosts.
- Typed `@ConfigurationProperties` with `@Validated`. No secrets in YAML.

## Health

Actuator, with `management.endpoints.web.base-path=/api`, `management.endpoints.web.path-mapping.health=health` and `management.endpoint.health.probes.enabled=true`. Liveness is `/api/health/liveness` and readiness `/api/health/readiness`. Expose only `health` (and `info`) publicly.

## Testing

- **Unit:** JUnit 5 + AssertJ for domain rules, named by FR.
- **Web slice:** `@WebMvcTest` + MockMvc for controllers.
- **Integration:** `@SpringBootTest` + Testcontainers Postgres with `@ServiceConnection` (Flyway applies migrations). Cover success, 400, 401, 403, 404, each errorCode and 409.

## Gate

CI runs, from the backend path (`./mvnw` instead of `mvn` when the repo has the wrapper):
```bash
mvn -B spotless:check verify
mvn -B -Dtest=OpenApiExportIT test && git diff --exit-code openapi/api.json
```

## Containerise

`Dockerfile` in the backend path, multi-stage:
- **Build:** `maven:<ver>-eclipse-temurin-<LTS>` (or `eclipse-temurin:<LTS>-jdk` with the wrapper) runs `mvn -B -DskipTests package`.
- **Runtime:** `eclipse-temurin:<LTS>-jre`, a non-root user (`RUN useradd -r -u 10001 app` then `USER app`), `COPY` the jar to `app.jar`, `ENV JAVA_TOOL_OPTIONS=-XX:MaxRAMPercentage=75`, `EXPOSE 8080`, `ENTRYPOINT ["java", "-jar", "app.jar"]`.
- `.dockerignore` excludes `target/` and local config.

## Deploy notes

- **Render:** Docker web service, health check path `/api/health/readiness`.
- **Railway:** the Dockerfile, health check path `/api/health/readiness`.
- **Not supported** on Vercel, Netlify, Cloudflare or GitHub Pages.
- **Memory:** keep the JVM within the platform's limits.

## Checklist

- [ ] Domain logic is testable without Spring, controllers are thin, and DTOs are validated records.
- [ ] ProblemDetail carries the spec's errorCodes, and the security chain matches the origin model.
- [ ] `DATABASE_URL` conversion is handled, and config is typed and validated.
- [ ] The contract export is fresh, operation IDs are stable, and the gate passes.
