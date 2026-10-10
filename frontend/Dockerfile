# syntax=docker/dockerfile:1
# AIDE web image: Vite build served by unprivileged nginx, which also proxies /api to the api Service.
# Build context: frontend/   (docker build -f frontend/Dockerfile frontend)

FROM node:25-slim AS build
WORKDIR /app
ENV CI=true
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.30-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
