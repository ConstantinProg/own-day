# OwnDay — GitHub Deployment Variables and Secrets

## Overview

OwnDay uses GitHub Actions variables for deployment settings and Environment secrets for credentials.
The [deployment workflow](../../.github/workflows/deploy.yml) is the source of truth for their usage.

Production deployment is associated with the GitHub environment:

`production`

The environment represents the production VPS running OwnDay.

## GitHub Environment

Location in the repository settings:

`Settings → Environments → production`

Deployment jobs that require access to production credentials must explicitly use the `production` environment.

## Environment Variables

Configure the following required variables in:

`Settings → Environments → production → Environment variables`

| Variable      | Purpose                                                                |
| ------------- | ---------------------------------------------------------------------- |
| `VPS_HOST`    | Public IP address or hostname of the production VPS.                    |
| `VPS_USER`    | Linux user used by GitHub Actions to connect to the VPS over SSH.        |
| `DEPLOY_PATH` | Absolute directory on the VPS containing `compose.yaml` and `.env`.     |

The workflow reads these values through `vars`. Creating only same-named secrets does not supply them.

### VPS_HOST

Contains the public address of the production VPS.

Example format:

`203.0.113.10`

or:

`server.example.com`

The actual value must never be committed to the repository if it is intentionally treated as deployment-sensitive configuration.

### VPS_USER

Contains the Linux account used for automated deployment.

OwnDay production deployment currently uses:

`deploy`

The account should have only the permissions required to deploy and manage OwnDay.

### DEPLOY_PATH

Contains the absolute path to the deployment directory, for example `/opt/ownday`.
The workflow changes to this directory before updating `.env` and running Docker Compose.
The directory must already contain `compose.yaml` and `.env` and be accessible to `VPS_USER`.

## Environment Secrets

Configure `VPS_SSH_KEY` in:

`Settings → Environments → production → Environment secrets`

### VPS_SSH_KEY

Contains the complete private SSH key used by GitHub Actions.

The corresponding public key must be installed on the VPS for the deployment user in:

`~/.ssh/authorized_keys`

The private key must:

* never be committed to Git;
* never be included in documentation;
* never be printed in GitHub Actions logs;
* be used only for deployment where practical.

## Runtime Secrets

Application runtime secrets are not currently stored in GitHub Actions.

These include:

| Configuration             | Purpose                                                         |
| ------------------------- | --------------------------------------------------------------- |
| `Telegram__BotToken`      | Authentication token for the OwnDay Telegram bot.               |
| `Telegram__BotUsername`   | Telegram username used to identify commands addressed to OwnDay. |
| `Telegram__WebhookSecret` | Secret used to authenticate incoming Telegram webhook requests. |

These values are stored directly on the production VPS and supplied to the OwnDay container through environment configuration.

They must not be committed to the repository.

## Database migration

The production image contains an EF Core migration bundle. The deployment workflow runs it as
the one-shot `migrate` Compose service after PostgreSQL becomes healthy and before it starts a
new application container. The bundle records completed migrations in EF Core's migration history,
so it applies only migrations that have not yet run.

```sh
docker compose pull app migrate postgres
docker compose up -d postgres
docker compose run --rm migrate
docker compose up -d app --remove-orphans
```

Copy [`deploy/compose.yaml`](../../deploy/compose.yaml) to `compose.yaml` in the directory specified
by `DEPLOY_PATH` before the first deployment (for example `/opt/ownday/compose.yaml`). Run the
commands above from that directory. The bundle reads `ConnectionStrings__Default` from the VPS `.env`; do not put
production connection strings in the image or repository. The VPS must use an x86-64 Linux host,
which matches the `linux-x64` bundle target.

The application does not apply migrations during startup. Do not use a bundle target such as `0`
in production without a reviewed rollback plan because it executes `Down` operations and may
delete data.

## Configuration Ownership

The current configuration separates deployment settings and credentials from application runtime credentials:

GitHub `production` environment variables:

* `VPS_HOST`
* `VPS_USER`
* `DEPLOY_PATH`

GitHub `production` environment secrets:

* `VPS_SSH_KEY`

Production VPS:

* `Telegram__BotToken`
* `Telegram__BotUsername`
* `Telegram__WebhookSecret`
* database credentials and other future runtime secrets

This keeps GitHub Actions responsible for deployment while the VPS remains responsible for application runtime configuration.

## GitHub Actions Usage

A deployment job that accesses the VPS secrets must target:

`environment: production`

The workflow uses these GitHub Actions expressions:

* `vars.VPS_HOST`
* `vars.VPS_USER`
* `vars.DEPLOY_PATH`
* `secrets.VPS_SSH_KEY`

Build and test jobs should not require access to these production credentials.

## Security Rules

1. Never commit actual secret values.
2. Never put private keys or Telegram tokens in workflow files.
3. Never print secrets during deployment.
4. Keep production secrets scoped to the `production` environment.
5. Use a dedicated SSH key for automated deployment.
6. Keep the corresponding private SSH key only in GitHub Environment secrets.
7. Store application runtime secrets on the production VPS.
8. Rotate credentials immediately if a secret is accidentally exposed.
9. Do not store real secret values in this document.

## Current Production Configuration

GitHub environment:

`production`

Required Environment variables:

* `VPS_HOST`
* `VPS_USER`
* `DEPLOY_PATH`

Required Environment secrets:

* `VPS_SSH_KEY`

Runtime Telegram secrets:

* stored on the VPS;
* not stored in GitHub;
* not committed to Git.

## Future Secrets

Additional production deployment secrets should be added to the `production` environment only when GitHub Actions actually requires access to them.

Application-only secrets should remain runtime configuration on the VPS unless the deployment architecture changes.
