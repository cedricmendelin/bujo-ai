# Copilot instructions for Bujo-AI

Bujo-AI is an AI-assisted bullet journal: Blazor PWA, ASP.NET Core API and remote
MCP, Azure SQL, Azure Container Apps (Terraform with Azure Verified Modules).
Read `.squad/decisions.md`, `docs/concept.md`, and `docs/workflow.md` before
changing anything.

## Implementing an issue

- Implement **only** the owner-approved plan: the newest issue comment starting
  with `## 📋 Implementation plan v` while the issue has the `plan:approved`
  label. If no approved plan exists, stop and say so in the PR.
- If you must deviate from the plan, keep the deviation minimal and list it
  under a `## Deviations from plan` heading in the PR description.
- Reference the issue (`Closes #N`) and the plan version in the PR description.
- Add focused automated tests for new behavior; run the build, tests, and
  `terraform fmt`/`validate` (when `infra/` changes) before finishing.
- Never commit secrets, tenant/subscription IDs, connection strings, or personal
  data. Azure access from GitHub Actions uses OIDC only.
- Do not change `.github/workflows/`, `.github/agents/`, or `.squad/` unless the
  approved plan explicitly lists those files.
- Do not pull later-release scope (for example release 2 AI search/RAG) into
  the change.

## Addressing review feedback

Squad Review checks (Architect, Security, Verifier, DevOps) post blocking
findings. Fix each blocking finding or explain in the PR why it is incorrect;
do not silence checks or weaken tests.
