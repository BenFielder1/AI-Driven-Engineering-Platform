<!-- Adapted from stack-packs data-jpa-flyway@1.0.0 skills/data-jpa-flyway/SKILL.md, snapshot 2026-10-25 -->

# Spring Data JPA + Flyway (PostgreSQL)

## Detect
Mirror the existing JPA and Flyway dependencies, `db/migration`, `spring.jpa.*` settings and entity/repository style.

## Compatible with
`be-spring-boot`. Also Supabase host mode, with Flyway owning the migrations.

## Setup
- `spring-boot-starter-data-jpa`, `flyway-core`, `flyway-database-postgresql`, `postgresql`; current stable versions you are confident exist, Java pinned in the build toolchain.
- `spring.jpa.hibernate.ddl-auto: validate` (never `update`/`create`/`create-drop` outside test config), `spring.jpa.open-in-view: false`, Flyway on locally and in tests.
- `application-migrate.yml`: `spring.main.web-application-type: none` with Flyway on, so the app migrates and exits. The serving profile sets `spring.flyway.enabled: false` in production.

## Modelling
- Entities are `<Name>Entity.java` in `domain` packages: `@Table(name = "snake_case")`, explicit `@Column(name, length, nullable, precision, scale)`.
- `UUID` (`GenerationType.UUID`) or `IDENTITY` keys. `LAZY` relationships, `@EntityGraph`/join fetches, record projections for reads.
- `@Version Long version` on concurrent aggregates; `OptimisticLockingFailureException` → 409.
- `Instant`/`OffsetDateTime` in UTC, `EnumType.STRING`, `citext` or a unique `lower(col)` index for case-insensitive uniqueness.
- Repositories (backend code): `JpaRepository`, parameterised `@Query`, `@Transactional(readOnly = true)` on reads.

## Migrations
- **One migration per step:** hand-written `src/main/resources/db/migration/V<n>__<description>.sql`, `<n>` = next integer after the base commit's highest. `R__` only for views and functions.
- Change entities and SQL together; `validate` fails when they disagree.
- `CREATE INDEX CONCURRENTLY` alone in a migration with `-- flyway:executeInTransaction=false`.
- **Expand/contract** for breaking changes. **Never edit a base-commit versioned migration** (checksums fail).
- **Migrate command:** `java -jar app.jar --spring.profiles.active=migrate`, the platform's pre-deploy step. Flyway-on-start only with a single replica, recorded in the spec.
- **CI:** integration tests (Flyway migrates a Testcontainers Postgres, `validate` checks entities).

## Local database
`postgres:<major>` pinned to match production, `POSTGRES_*`, `PGDATA` in a volume subdirectory, `pg_isready`, port 5432, named volume.

## Testing
`@SpringBootTest`/`@DataJpaTest` with `replace = NONE`, Testcontainers `PostgreSQLContainer` + `@ServiceConnection`, shared via a base class or `@ImportTestcontainers`; truncate or roll back between tests.

## Production notes
- Convert `postgres://` (Render, Railway `${{Postgres.DATABASE_URL}}`) to `jdbc:postgresql://…?sslmode=require`; Hikari `maximumPoolSize` within limits.
- Supabase host mode: session pooler (`:5432`), or the transaction pooler with `prepareThreshold=0`; never `db.<ref>.supabase.co`.

## Checklist
- [ ] `validate`, open-in-view off, explicit entities, `@Version`, UTC.
- [ ] One expand/contract-safe `V<n>__` per step; base-commit migrations untouched.
- [ ] Migrate profile is the pre-deploy step; Testcontainers tests pass; no N+1.
