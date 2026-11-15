# Copilot instructions for Bujo-AI

Bujo-AI is an AI-assisted bullet journal: Blazor PWA, ASP.NET Core API and remote
MCP, Azure SQL, Azure Container Apps (Terraform with Azure Verified Modules).
Read `.squad/decisions.md`, `docs/concept.md`, and `docs/workflow.md` before
changing anything.

## Implementing an issue

- Implement **only** the verified owner-approved plan: an owner- or
  `github-actions[bot]`-authored versioned issue plan with a bot approval record
  binding its exact comment/version/SHA-256 and owner approval timeline event.
  Both `plan:approved` and `plan:bound` are necessary but not sufficient;
  verify live state with `node .github/scripts/plan-approval.js verify --issue <N>`
  from trusted default-branch code before starting. Publishing a plan never
  authorizes implementation. For a PR labeled `plan:waived` by the owner,
  implement the exact approved PR description instead. If neither scope can be
  verified, stop and say so in the PR.
- Every PR description must include exactly one whole-line declaration:
  `Completion: full` or `Completion: partial`. A full PR may close its issue
  only when it provides evidence for every acceptance criterion, including code
  and relevant tests. A plan's partial/specification-only fallback allows only
  a partial delivery; unmet completion criteria remain blocking and cannot be
  downgraded to advisory.
- A partial PR must use a non-closing issue reference such as `Relates to #N`,
  state the unfinished work and its dependency under `Remaining work`, and
  leave the issue open. Remove closing references from the entire description,
  including any Copilot-generated suffix.
- If you must deviate from the plan, keep the deviation minimal and list it
  under a `## Deviations from plan` heading in the PR description.
- Reference the issue and plan version in the PR description; use `Closes #N`
  only for a complete delivery and a non-closing reference for a partial one.
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

### PR-description fixes

These rules apply to every implementing team member, not to read-only reviewers.
Use a native PR-update tool if available, then read the PR again to verify the
description actually changed. A comment describing an edit is not an edit.
Do not request the owner PAT, broaden MCP permissions, or use an unauthenticated
browser as a workaround.

If a cloud session cannot update metadata, use its existing PR-comment reply
tool to publish a standalone `<!-- squad-pr-metadata -->` line followed by one
fenced `json` object with exactly `headSha`, `baseBodySha256`, and `body`.
Read the current PR head and exact description through the GitHub read tools;
hash that description's UTF-8 bytes with SHA-256. `body` is the full replacement
description and must begin with `Completion: full` or `Completion: partial`.
Preserve the approved issue reference and plan version. Escape JSON correctly;
include no trailing text after the fence. See `docs/workflow.md` for the format.

The trusted shepherd applies valid proposals and restarts reviews only after a
successful latest session. Report a proposal as pending, never as already
applied. A stale/rejected proposal or a no-progress session escalates to
`needs:human`. For `plan:waived`, only the owner may propose a replacement:
the description itself is the approved scope.

## PR lifecycle (see README → How we work)

- **Cloud (Copilot cloud agent):** work on the draft PR Copilot creates. Do not
  mark it ready or request reviews; when your session ends, Squad PR Shepherd
  does that and starts CI and Squad Review. `@copilot` comments from the Squad
  Review Gate or shepherd are fix or conflict requests: handle every blocking
  finding in one session.
- **Local (GitHub Copilot app):** open a ready (non-draft) PR with the
  completion declaration, issue reference, and plan version; the owner turns on
  Agent merge. If the work has no issue plan, state its scope in the PR
  description and ask the owner to apply `plan:waived`.
- The gate squash auto-merges when every check passes; never merge yourself.

## Human in the loop

The human in the loop is @cedricmendelin. When you need something only a human
can do (a secret, an Azure or repository setting, a product decision, a plan
change), do not guess or work around it:

- Add a `## 🙋 Needs human` section to the PR description that states exactly
  what is needed and mentions @cedricmendelin.
- In a local session, for one-time setup outside the PR, also create an issue
  labeled `needs:human` and assigned to @cedricmendelin with a checklist.
