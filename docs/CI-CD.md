# CI/CD Documentation

Complete guide for continuous integration and deployment workflows.

## Table of Contents

- [Overview](#overview)
- [Workflows](#workflows)
- [Setup](#setup)
- [Usage](#usage)
- [Troubleshooting](#troubleshooting)

---

## Overview

This project uses GitHub Actions for automated testing, building, and deployment.

### What's Automated

- ✅ **Build & Test** - Every PR and push
- ✅ **Code Quality** - Formatting and security scans
- ✅ **Docker Images** - Multi-arch builds (amd64, arm64)
- ✅ **NuGet Packages** - Automated publishing
- ✅ **Dependency Updates** - Weekly via Dependabot
- ✅ **Staging Deployment** - Auto-deploy dev/main via Ansible

### Branch Strategy

```
main (production)
  ↑
dev (integration)
  ↑
feature/* (development)
```

---

## Workflows

### 1. CI - Build and Test

**File**: `.github/workflows/ci-build-test.yml`

**When**: PR or push to dev/main

**What it does**:
1. Builds solution (Release mode)
2. Runs all tests with coverage
3. Uploads test results
4. Comments results on PRs

**Run locally**:
```bash
make build
make test-coverage
```

---

### 2. Code Quality & Security

**File**: `.github/workflows/code-quality.yml`

**When**: PR, push, or weekly (Mondays)

**What it does**:
- **CodeQL**: Security vulnerability scanning
- **Formatting**: Validates code formatting with `dotnet format`
- **Dependencies**: Reviews for vulnerable packages

**Fix formatting locally**:
```bash
dotnet format
```

---

### 3. Docker Build & Push

**Files**:
- `docker-build-push-dev.yml` (dev branch)
- `docker-build-push-release.yml` (releases)

**What it builds**:
- `nethermind-circlesubi` (Circles plugin)
- `pathfinder-host` (Pathfinder service)
- `rpc-host` (RPC service)

**Platforms**: linux/amd64, linux/arm64

**Tags**:
- Dev: `username/image:dev`
- Release: `username/image:v1.2.3` + `username/image:latest`

**Build caching**: Enabled for faster builds (2-5 min vs 15-30 min)

**Build locally**:
```bash
make docker
```

---

### 4. NuGet Publishing

**File**: `.github/workflows/nuget-publish.yml`

**When**: GitHub release created

**What it does**:
1. Builds solution
2. Runs tests (fails if tests fail)
3. Creates NuGet packages
4. Publishes to NuGet.org
5. Uploads artifacts (90 days)

**Create packages locally**:
```bash
make pack
```

---

### 5. Staging Deployment

**File**: `.github/workflows/deploy-staging.yml`

**When**: Push to dev or main (after Docker build completes)

**What it does**:

1. Waits for Docker images to be built and pushed
2. Triggers Ansible playbook in external repo via `repository_dispatch`
3. Passes environment, image tag, commit info to Ansible

**Environment mapping**:

- `dev` branch → staging environment (`image:dev`)
- `main` branch → production environment (`image:latest`)

**Manual deployment**:

```bash
gh workflow run deploy-staging.yml --field environment=staging
```

**Required secrets**:

| Secret | Purpose |
|--------|---------|
| `ANSIBLE_REPO_PAT` | Personal Access Token with `repo` scope for Ansible repo |
| `ANSIBLE_REPO` | Repository path (e.g., `org/ansible-infra`) |

See [Ansible Integration](#ansible-integration) for Ansible repo setup.

---

### 6. Dependabot

**File**: `.github/dependabot.yml`

**Schedule**: Weekly on Mondays

**Updates**:
- GitHub Actions
- NuGet packages (grouped: Nethermind, test deps)
- Docker base images

**Manage PRs**:
```bash
# List Dependabot PRs
gh pr list --author app/dependabot

# Merge PR
gh pr merge 123 --auto --squash

# Close/ignore PR
@dependabot ignore  # Comment on PR
```

---

## Setup

### Required Secrets

Add these in Settings → Secrets and variables → Actions:

| Secret | Where to Get | Purpose |
|--------|--------------|---------|
| `DOCKERHUB_USERNAME` | Docker Hub account | Docker authentication |
| `DOCKERHUB_TOKEN` | Docker Hub → Settings → Security → New Access Token | Push images |
| `NUGET_API_KEY` | NuGet.org → API Keys → Create | Publish packages |

**Add secrets via CLI**:
```bash
gh secret set DOCKERHUB_USERNAME --body "your-username"
gh secret set DOCKERHUB_TOKEN      # Will prompt
gh secret set NUGET_API_KEY        # Will prompt
```

### Docker Hub Token

1. Go to [hub.docker.com](https://hub.docker.com)
2. Settings → Security → New Access Token
3. Name: `GitHub Actions`
4. Permissions: **Read & Write**
5. Generate and copy token
6. Add to GitHub secrets

### NuGet API Key

1. Go to [nuget.org](https://www.nuget.org)
2. API Keys → Create
3. Settings:
   - Name: `GitHub Actions`
   - Glob pattern: `*` (or specific packages)
   - Scopes: **Push**
   - Expiration: 365 days
4. Create and copy key
5. Add to GitHub secrets

---

## Usage

### Creating a Release

Release workflow publishes both Docker images and NuGet packages:

**1. Update versions**:
```bash
# Update version in .csproj files
<Version>1.2.3</Version>

git add .
git commit -m "Bump version to 1.2.3"
git push origin dev
```

**2. Create and push tag**:
```bash
git tag -a v1.2.3 -m "Release v1.2.3"
git push origin v1.2.3
```

**3. Create GitHub release**:
```bash
gh release create v1.2.3 \
  --title "v1.2.3" \
  --notes "## What's Changed
- Added feature X
- Fixed bug Y"
```

Or use GitHub UI: Releases → Draft a new release

**4. Monitor workflows**:
- Go to Actions tab
- Watch "Docker Build & Push (Release)" and "Publish NuGet Packages"

### Manual Workflow Runs

```bash
# Run CI manually
gh workflow run ci-build-test.yml --ref dev

# Run code quality checks
gh workflow run code-quality.yml --ref dev

# Trigger Docker dev build
git commit --allow-empty -m "Trigger build"
git push origin dev

# Trigger NuGet publish manually
gh workflow run nuget-publish.yml --field version=1.2.3
```

### Viewing Workflow Status

```bash
# List recent runs
gh run list --limit 10

# Watch running workflow
gh run watch

# View logs
gh run view --log

# View failed logs only
gh run view --log-failed
```

### Testing Locally Before Push

```bash
# Full CI simulation
make clean
dotnet restore
dotnet build -c Release --no-restore
./scripts/test.sh --coverage
dotnet format --verify-no-changes

# Check for vulnerabilities
dotnet list package --vulnerable

# Build Docker images
make docker

# Create NuGet packages
make pack
```

---

## Troubleshooting

### Build Failures

**Tests failing**:
```bash
# Run tests locally
./scripts/test.sh --coverage

# View test results
ls TestResults/
```

**Build errors**:
```bash
# Clean and rebuild
make clean
make build

# Check for missing dependencies
dotnet restore
```

**Formatting issues**:
```bash
# Fix formatting
dotnet format

# Verify formatting
dotnet format --verify-no-changes
```

### Docker Issues

**"No space left on device"**:
```bash
# Clean Docker system
docker system prune -af --volumes
```

**Authentication failed**:
- Verify `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` are set
- Check token has Read & Write permissions
- Regenerate token if needed

**Multi-arch build hangs**:
- ARM builds can take 2-3x longer than AMD64
- Wait patiently or disable ARM temporarily:
  ```yaml
  platforms: linux/amd64  # Remove arm64
  ```

**Cache issues**:
- Delete buildcache tags on Docker Hub
- Next build will be slower but fresh

### NuGet Issues

**"Package version already exists"**:
- NuGet versions are immutable
- Increment version number
- Create new release

**Authentication failed**:
- Verify `NUGET_API_KEY` is set correctly
- Check key hasn't expired
- Regenerate key on NuGet.org

**Tests fail before publishing**:
- Workflow intentionally fails if tests fail
- Fix tests first, then create new release

### Secrets Issues

**Secret not working**:
```bash
# List secrets (names only)
gh secret list

# Verify secret name spelling in workflow file
# Must match exactly (case-sensitive)
```

**Secret appears empty**:
- Secrets aren't available to fork PRs (security)
- Re-create secret if recently added
- Check workflow uses correct secret name

### Dependabot Issues

**Too many PRs**:
```yaml
# Reduce in .github/dependabot.yml
open-pull-requests-limit: 5
```

**PRs always fail**:
- Review breaking changes in release notes
- Test locally
- Ignore problematic version: `@dependabot ignore this major version`

**PR outdated**:
- Comment: `@dependabot rebase`

### CodeQL Issues

**Build fails in CodeQL**:
- Ensure project builds locally first
- CodeQL analyzes during build

**False positives**:
- Go to Security → Code scanning alerts
- Dismiss alert with reason

### General Workflow Issues

**Workflow won't trigger**:
```bash
# Check workflow syntax
gh workflow view ci-build-test.yml

# List recent runs
gh run list --workflow=ci-build-test.yml
```

**Need debug logs**:
- Actions tab → Workflow run
- Re-run jobs → Enable debug logging

---

## Common Commands

### Workflow Management

```bash
# List workflows
gh workflow list

# View workflow runs
gh run list --workflow=ci-build-test.yml --limit 10

# Watch running workflow
gh run watch

# View specific run
gh run view 123456789

# Re-run failed jobs
gh run rerun 123456789 --failed

# Cancel running workflow
gh run cancel 123456789
```

### Release Management

```bash
# List releases
gh release list

# Create release
gh release create v1.2.3 --generate-notes

# Delete release
gh release delete v1.2.3 --yes

# View release
gh release view v1.2.3
```

### Pull Request Checks

```bash
# View PR check status
gh pr checks 123

# Watch checks in real-time
gh pr checks 123 --watch
```

### Docker Commands

```bash
# Pull dev images
docker pull username/nethermind-circlesubi:dev
docker pull username/pathfinder-host:dev
docker pull username/rpc-host:dev

# Pull release images
docker pull username/nethermind-circlesubi:latest
docker pull username/pathfinder-host:v1.2.3
```

### Local Development

```bash
# Build
make build

# Test
make test
make test-coverage

# Clean
make clean

# Docker
make docker
make docker-up
make docker-down
make docker-logs

# NuGet
make pack
make push

# Full release workflow
make release
```

---

## Configuration

### Package Configuration (.csproj)

To publish a NuGet package, add to `.csproj`:

```xml
<PropertyGroup>
  <IsPackable>true</IsPackable>
  <PackageId>YourPackageName</PackageId>
  <Version>1.0.0</Version>
  <Authors>Your Name</Authors>
  <Description>Package description</Description>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageProjectUrl>https://github.com/yourorg/repo</PackageProjectUrl>
  <PackageTags>circles;nethermind;ethereum</PackageTags>
</PropertyGroup>
```

### Workflow Customization

**Change .NET version**:
```yaml
# In workflow files
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '8.0.x'  # Change this
```

**Change test command**:
```yaml
# In ci-build-test.yml
- name: Run tests
  run: ./scripts/test.sh --coverage  # Modify this
```

**Add workflow timeout**:
```yaml
jobs:
  build-and-test:
    timeout-minutes: 30  # Add this
```

### Dependabot Customization

**Change schedule**:
```yaml
# In .github/dependabot.yml
schedule:
  interval: "daily"    # or "weekly", "monthly"
  day: "monday"
```

**Add dependency group**:
```yaml
groups:
  your-group:
    patterns:
      - "Package.Name.*"
```

**Ignore specific dependency**:
```yaml
ignore:
  - dependency-name: "Package.Name"
    versions: ["2.x"]
```

---

## Best Practices

### For Developers

1. ✅ Run tests locally before pushing
2. ✅ Format code with `dotnet format`
3. ✅ Review CI failures - don't ignore
4. ✅ Keep PRs small and focused
5. ✅ Update tests with code changes

### For Releases

1. ✅ Test thoroughly before releasing
2. ✅ Use semantic versioning (major.minor.patch)
3. ✅ Write clear release notes
4. ✅ Document breaking changes
5. ✅ Monitor workflow completion

### For Security

1. ✅ Rotate secrets annually
2. ✅ Review Dependabot PRs promptly
3. ✅ Address CodeQL alerts quickly
4. ✅ Use least-privilege tokens
5. ✅ Never commit secrets to code

### For Maintenance

1. ✅ Keep dependencies updated
2. ✅ Review and merge Dependabot PRs weekly
3. ✅ Monitor workflow performance
4. ✅ Clean up old workflow runs
5. ✅ Update documentation with changes

---

## Performance Tips

### Speed Up Builds

1. ✅ Build caching enabled (Docker)
2. ✅ Parallel job execution
3. ✅ Restore dependency caching
4. Skip CI on docs-only changes:
   ```yaml
   on:
     push:
       paths-ignore:
         - '**.md'
         - 'docs/**'
   ```

### Reduce CI Minutes

- Use matrix builds sparingly
- Skip redundant workflow runs
- Enable auto-merge for Dependabot
- Use conditional jobs:
  ```yaml
  if: github.event_name == 'push'
  ```

---

## Status Badges

Add to README.md to show build status:

```markdown
[![CI](https://github.com/yourorg/repo/actions/workflows/ci-build-test.yml/badge.svg)](https://github.com/yourorg/repo/actions/workflows/ci-build-test.yml)
[![Code Quality](https://github.com/yourorg/repo/actions/workflows/code-quality.yml/badge.svg)](https://github.com/yourorg/repo/actions/workflows/code-quality.yml)
```

---

## Quick Reference

### Workflow Triggers

| Workflow | Trigger |
|----------|---------|
| CI Build & Test | PR, Push to dev/main |
| Code Quality | PR, Push, Weekly |
| Docker Dev | Push to dev |
| Docker Release | GitHub Release |
| NuGet Publish | GitHub Release |
| Deploy Staging | Push to dev/main (after Docker build) |

### Make Commands

```bash
make help              # Show all commands
make build             # Build solution
make test              # Run tests
make test-coverage     # Run tests with coverage
make clean             # Clean build artifacts
make docker            # Build Docker images
make docker-up         # Start Docker services
make docker-down       # Stop Docker services
make pack              # Create NuGet packages
make push              # Push NuGet packages
make all               # Build, test, pack
make release           # Build, test, pack, push
```

### GitHub CLI Commands

```bash
gh workflow list                    # List workflows
gh workflow run <name>              # Run workflow
gh run list                         # List workflow runs
gh run watch                        # Watch current run
gh run view --log                   # View logs
gh pr checks                        # View PR checks
gh release create <tag>             # Create release
gh secret set <name>                # Set secret
gh secret list                      # List secrets
```

---

## Ansible Integration

To auto-deploy from this repo to staging/production via Ansible:

### 1. Create Personal Access Token

1. Go to GitHub → Settings → Developer settings → Personal access tokens → Fine-grained tokens
2. Create token with:
   - **Repository access**: Select your Ansible repo
   - **Permissions**: Contents (Read and write), Actions (Read and write)
3. Add as `ANSIBLE_REPO_PAT` secret in this repo

### 2. Set Ansible Repo Secret

```bash
gh secret set ANSIBLE_REPO --body "yourorg/ansible-infrastructure"
```

### 3. Ansible Repo Setup

In your Ansible repository, create `.github/workflows/deploy-circles.yml`.
See `docs/ansible-workflow-example.yml` in this repo for a complete example.

**Required secrets in Ansible repo**:

| Secret | Purpose |
|--------|---------|
| `VAULT_PASSWORD` | Ansible vault password |
| `SSH_PRIVATE_KEY` | SSH key for server access |

**Required variables in Ansible repo** (Settings → Secrets and variables → Variables):

| Variable | Purpose |
|----------|---------|
| `STAGING_HOST` | Staging server hostname/IP |
| `PRODUCTION_HOST` | Production server hostname/IP |
| `SLACK_WEBHOOK_URL` | (Optional) Slack notifications |

### 4. Ansible Playbook Structure

```text
ansible-repo/
├── .github/workflows/
│   └── deploy-circles.yml      # Workflow that receives dispatch
├── inventories/
│   ├── staging/
│   │   └── hosts               # Staging inventory
│   └── production/
│       └── hosts               # Production inventory
├── playbooks/
│   └── deploy-circles.yml      # Deployment playbook
├── group_vars/
│   ├── staging/
│   │   └── vault.yml           # Encrypted staging vars
│   └── production/
│       └── vault.yml           # Encrypted production vars
└── roles/
    └── circles/
        └── tasks/main.yml      # Deployment tasks
```

### 5. Example Ansible Playbook

```yaml
# playbooks/deploy-circles.yml
- hosts: circles_servers
  become: yes
  vars:
    image_tag: "{{ image_tag | default('dev') }}"
  tasks:
    - name: Pull latest Docker images
      community.docker.docker_image:
        name: "{{ item }}"
        tag: "{{ image_tag }}"
        source: pull
        force_source: yes
      loop:
        - "{{ dockerhub_user }}/nethermind-circlesubi"
        - "{{ dockerhub_user }}/pathfinder-host"
        - "{{ dockerhub_user }}/rpc-host"

    - name: Restart services
      community.docker.docker_compose_v2:
        project_src: /opt/circles
        state: present
        pull: never
        recreate: always
```

### Deployment Flow

```text
Push to dev/main
      ↓
Docker Build & Push (this repo)
      ↓
Deploy to Staging workflow (this repo)
      ↓
repository_dispatch event
      ↓
Ansible repo receives event
      ↓
Ansible playbook runs
      ↓
Services updated on target server
```

---

## Support

For workflow issues:

1. Check workflow logs in GitHub Actions tab
2. Review this documentation
3. Test locally using make commands
4. Check [GitHub Actions docs](https://docs.github.com/en/actions)
5. Create issue in repository

For specific issues:
- Build failures → Check test logs
- Docker issues → Check Docker Hub
- NuGet issues → Check NuGet.org
- Security issues → Check Security tab
