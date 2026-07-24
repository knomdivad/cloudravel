# syntax=docker/dockerfile:1
# ============================================================================
# Next.js static export (output: 'export') served by nginx.
# nginx also reverse-proxies /api → the Functions container, so the browser
# talks to a single origin and there is no CORS to configure.
#
# NEXT_PUBLIC_* values are baked at BUILD time — they must be passed as build
# args (docker compose does this from the .env file).
# ============================================================================

FROM docker.io/library/node:20-alpine AS build
WORKDIR /app

# Prefer IPv4 for outbound DNS lookups. Some networks resolve registry hosts
# (npmjs.org, Google Fonts if next/font is used, etc.) to an IPv6 address that
# silently blackholes — no refusal, just a ~20-75s timeout per connection
# before falling back to IPv4 — which, multiplied across hundreds of package
# requests, turns a ~1 minute `npm ci` into 10-20x that. This affects the whole
# build stage (npm ci AND npm run build), not just one command.
ENV NODE_OPTIONS=--dns-result-order=ipv4first

COPY package*.json ./
# BuildKit cache mount: persists npm's download cache across builds, even when
# package.json changes invalidate the layer above and force a fresh `npm ci` —
# so a package.json edit costs re-resolving the tree, not re-downloading every
# package from the registry again.
RUN --mount=type=cache,target=/root/.npm \
    npm ci --no-audit --no-fund

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
FROM docker.io/library/nginx:alpine
# The nginx image runs envsubst over /etc/nginx/templates/*.template at startup
# and writes the result to /etc/nginx/conf.d/. API_UPSTREAM defaults to the
# compose service `api:80`; Kubernetes overrides it with the api Service name.
ENV API_UPSTREAM=api:80
COPY default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/out /usr/share/nginx/html
