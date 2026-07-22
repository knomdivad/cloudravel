# ============================================================================
# Next.js static export (output: 'export') served by nginx.
# nginx also reverse-proxies /api → the Functions container, so the browser
# talks to a single origin and there is no CORS to configure.
#
# NEXT_PUBLIC_* values are baked at BUILD time — they must be passed as build
# args (docker compose does this from the .env file).
# ============================================================================

FROM node:20-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .

# Public build-time config. API base is relative (/api) because nginx proxies it.
ARG NEXT_PUBLIC_API_BASE_URL=/api
ARG NEXT_PUBLIC_AZURE_AD_CLIENT_ID=""
ARG NEXT_PUBLIC_AZURE_AD_TENANT_ID=""
ARG NEXT_PUBLIC_API_SCOPE=""
ENV NEXT_PUBLIC_API_BASE_URL=$NEXT_PUBLIC_API_BASE_URL \
    NEXT_PUBLIC_AZURE_AD_CLIENT_ID=$NEXT_PUBLIC_AZURE_AD_CLIENT_ID \
    NEXT_PUBLIC_AZURE_AD_TENANT_ID=$NEXT_PUBLIC_AZURE_AD_TENANT_ID \
    NEXT_PUBLIC_API_SCOPE=$NEXT_PUBLIC_API_SCOPE

RUN npm run build   # produces the static site in ./out

# ----------------------------------------------------------------------------
FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/out /usr/share/nginx/html
