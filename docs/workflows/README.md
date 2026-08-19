# GitHub Actions Workflow Files

The two CI/CD workflow files live in this directory as copies. They're
intended to be placed at `.github/workflows/ci.yml` and
`.github/workflows/cd-deploy.yml` in the repository root.

## Why they're here

GitHub requires a PAT to have the `workflow` scope to push commits that
modify files under `.github/workflows/`. The PAT used for the initial
repo population did not have that scope, so the workflow files could
not be pushed via the standard `git push` flow.

## How to install them

Choose one:

### Option 1 — GitHub web UI (no additional auth)

1. Open the repo in the GitHub web UI.
2. Click **Add file → Create new file**.
3. Enter `.github/workflows/ci.yml` as the filename.
4. Paste the contents of [`docs/workflows/ci.yml`](./ci.yml).
5. Commit directly to `main`.
6. Repeat for `.github/workflows/cd-deploy.yml` using
   [`docs/workflows/cd-deploy.yml`](./cd-deploy.yml).

### Option 2 — PAT with `workflow` scope

1. Create a new fine-grained PAT (or classic PAT) with at least
   `Contents: read+write` and `Workflows: read+write` scopes.
2. Move the files locally:
   ```bash
   mv docs/workflows/ci.yml .github/workflows/ci.yml
   mv docs/workflows/cd-deploy.yml .github/workflows/cd-deploy.yml
   ```
3. Commit and push:
   ```bash
   git add .github/workflows/
   git commit -m "ci: add build/test + terraform deploy workflows"
   git push origin main
   ```
4. Optionally remove `docs/workflows/` once the canonical copies are in
   `.github/workflows/`.

## Workflow summaries

### `ci.yml`

Runs on push to `main` and on PRs. Steps:

1. `dotnet restore`
2. `dotnet format --verify-no-changes`
3. `dotnet build`
4. `dotnet test` (unit tests with coverage)
5. `dotnet test` (integration tests with Testcontainers MsSql — Docker
   socket on the GitHub runner)
6. Generate static OpenAPI spec via Swashbuckle CLI
7. Upload artifacts: coverage report, test results, OpenAPI spec, API
   binaries
8. Run `terraform plan` with stub secrets to validate the Terraform
   configuration structurally

### `cd-deploy.yml`

Runs on merge to `main` or via manual dispatch with an environment
choice (`staging` or `prod`). Steps:

1. Build + publish API binaries (`dotnet publish` with `linux-x64`
   runtime, framework-dependent)
2. Pack `latest.tar.gz` + SHA-256 checksum
3. Configure AWS credentials via OIDC (no static AWS keys)
4. `terraform init` → `terraform plan` with real secrets from GitHub
   Secrets
5. Manual approval gate for prod (uses `trstringer/manual-approval`)
6. `terraform apply`
7. Upload build to S3 for the ASG user-data script to fetch
8. Trigger ASG instance refresh with `MinHealthyPercentage=50` so a
   failed deploy rolls back automatically
9. Wait for the instance refresh to complete
