# OwnDay — GitHub Secrets

## Overview

OwnDay uses GitHub Actions Environment secrets to provide credentials required for production deployment.

Production deployment is associated with the GitHub environment:

`production`

The environment represents the production VPS running OwnDay.

## GitHub Environment

Location in the repository settings:

`Settings → Environments → production`

Deployment jobs that require access to production credentials must explicitly use the `production` environment.

## Environment Secrets

The following secrets are configured in:

`Settings → Environments → production → Environment secrets`

| Secret        | Purpose                                                                       |
| ------------- | ----------------------------------------------------------------------------- |
| `VPS_HOST`    | Public IP address or hostname of the production VPS.                          |
| `VPS_USER`    | Linux user used by GitHub Actions to connect to the VPS over SSH.             |
| `VPS_SSH_KEY` | Private SSH key used by GitHub Actions to authenticate on the production VPS. |

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

Apply EF Core migrations against the production PostgreSQL database before deploying a version that requires them:

```sh
dotnet ef database update \
  --project src/OwnDay.Infrastructure/OwnDay.Infrastructure.csproj \
  --startup-project src/OwnDay.Host/OwnDay.Host.csproj
```

The application does not apply migrations during startup.

## Secret Ownership

The current configuration separates deployment credentials from application runtime credentials:

GitHub `production` environment:

* `VPS_HOST`
* `VPS_USER`
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

The secrets are then available through GitHub Actions expressions:

`secrets.VPS_HOST`

`secrets.VPS_USER`

`secrets.VPS_SSH_KEY`

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

Configured Environment secrets:

* `VPS_HOST`
* `VPS_USER`
* `VPS_SSH_KEY`

Runtime Telegram secrets:

* stored on the VPS;
* not stored in GitHub;
* not committed to Git.

## Future Secrets

Additional production deployment secrets should be added to the `production` environment only when GitHub Actions actually requires access to them.

Application-only secrets should remain runtime configuration on the VPS unless the deployment architecture changes.
