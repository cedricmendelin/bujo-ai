# Bujo assistant skills

These versioned Markdown skills guide daily review, one-item capture, and
weekly-list draft review through Bujo's authenticated MCP tools. Open a skill
and copy its instructions into a client that supports user-provided skills or
prompts; follow that client's own installation instructions. Where skill
installation is unavailable, paste the instructions into an interactive
session. The client-specific installation and execution behavior is unverified
for every Copilot and Claude product listed in [the evidence matrix](../docs/mcp.md#compatibility-evidence).

| Skill | Version | Purpose | Manual equivalent |
|---|---:|---|---|
| [`bujo-daily-review`](skills/bujo-daily-review/SKILL.md) | 1.0.0 | Read daily-start results. | Open **Today** in the web app. |
| [`bujo-capture`](skills/bujo-capture/SKILL.md) | 1.0.0 | Capture one explicitly requested item. | Use **Capture** in the web app. |
| [`bujo-weekly-draft-review`](skills/bujo-weekly-draft-review/SKILL.md) | 1.0.0 | Review an existing weekly list and optionally create an approval proposal. | Open **Lists**, select the weekly list, and choose **Start review**. |

Skills use only the MCP tools listed in each file. They do not install
schedulers, store credentials, grant approval, or send journal content to
another integration. Only the authenticated user's existing authorization
applies. Review private journal content only in a client the user trusts.

Scheduled execution is not verified for any client. The
[read-only scheduled prompt template](scheduled/daily-review-readonly.md) must
not be enabled until a specific client/version is verified and recorded.

## Version history

- **1.0.0** — Initial daily review, capture, and weekly draft review skills;
  scheduled daily-review template marked unverified.
