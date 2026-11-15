# Copilot instructions for Bujo-AI

Bujo-AI is an AI-assisted bullet journal: Blazor PWA, ASP.NET Core API and remote
MCP, Azure SQL, Azure Container Apps (Terraform with Azure Verified Modules).
Read `.squad/decisions.md`, `docs/concept.md`, and `docs/workflow.md` before
changing anything.

## Implementing an issue

- Implement **only** the owner-approved plan: the newest issue comment starting
  with `## 📋 Implementation plan v` while the issue has the `plan:approved`
  label, or, for a PR labeled `plan:waived` by the owner, the scope in the PR
  description. If neither exists, stop and say so in the PR.
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

## PR lifecycle (see README → How we work)

- **Cloud (Copilot cloud agent):** work on the draft PR Copilot creates. Do not
  mark it ready or request reviews; when your session ends, Squad PR Shepherd
  does that and starts CI and Squad Review. `@copilot` comments from the Squad
  Review Gate or shepherd are fix or conflict requests: handle every blocking
  finding in one session.
- **Local (GitHub Copilot app):** open a ready (non-draft) PR with `Closes #N`
  and the plan version; the owner turns on Agent merge. If the work has no
  issue plan, state its scope in the PR description and ask the owner to apply
  `plan:waived`.
- The gate squash auto-merges when every check passes; never merge yourself.

## Human in the loop

The human in the loop is @cedricmendelin. When you need something only a human
can do (a secret, an Azure or repository setting, a product decision, a plan
change), do not guess or work around it:

- Add a `## 🙋 Needs human` section to the PR description that states exactly
  what is needed and mentions @cedricmendelin.
- In a local session, for one-time setup outside the PR, also create an issue
  labeled `needs:human` and assigned to @cedricmendelin with a checklist.
