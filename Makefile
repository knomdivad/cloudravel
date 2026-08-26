# CloudRavel — local / OrbStack stack convenience targets.
#
# Docker Compose has no config option to "always detach" — `up` stays attached
# unless you pass -d. These targets pass it for you, so the stack keeps running
# after you close the shell.
#
#   make up        # build images + start the stack DETACHED (safe to exit shell)
#   make start     # start DETACHED without rebuilding
#   make logs      # follow logs (Ctrl-C stops tailing; the stack keeps running)
#   make down      # stop and remove the stack
#   make restart   # rebuild + recreate, detached
#   make ps        # container status
#
# Requires a .env with MSSQL_SA_PASSWORD set (see .env.example); Compose reads it
# automatically.

COMPOSE ?= docker compose

# Short commit SHA, passed to the api build so the version shown in the UI
# includes it (see docker-compose.yml's api.build.args.GIT_SHA and
# src/backend/Dockerfile). Falls back to empty if git isn't available —
# the version just shows the bare semver in that case.
export GIT_SHA := $(shell git rev-parse --short HEAD 2>/dev/null)

.PHONY: up start build down restart logs ps

up:            ## Build images and start the stack detached
	$(COMPOSE) up -d --build

start:         ## Start the stack detached without rebuilding
	$(COMPOSE) up -d

build:         ## Build images only
	$(COMPOSE) build

down:          ## Stop and remove the stack
	$(COMPOSE) down

restart:       ## Rebuild and recreate the stack, detached
	$(COMPOSE) down
	$(COMPOSE) up -d --build

logs:          ## Follow logs (the stack keeps running when you Ctrl-C)
	$(COMPOSE) logs -f

ps:            ## Show container status
	$(COMPOSE) ps
