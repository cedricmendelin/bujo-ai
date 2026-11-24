# Ralph Instructions

Bujo-AI uses governed GitHub delivery, not manual bootstrap.

- On "Ralph go", monitor the authorized queue for the current session and report
  actionable handoffs to the coordinator. GitHub events and the existing PR
  controller's bounded reconciliation handle unattended progress.
- Route missing information and priorities to intake/planning. Initial implementation
  still requires the latest plan's `plan:approved`; labels never authorize scope.
- Routine implementation choices need no permission. Scope/contract, security/privacy,
  cost and dependency changes use an explicit request and owner decision under
  `docs/squad-approvals.md`. A replacement numbered plan is optional.
- Report actionable human blockers through GitHub with personal Teams notifications
  when separately configured. A notification is not a decision.
- Ralph detects and reports; the coordinator and existing workflows own dispatch,
  repair and continuation. Do not start a second assignment or repair authority.

No local watcher, unbounded polling, automatic retrospective, direct merge,
protection bypass or unapproved execution. Stop the in-session monitor when
requested or when no authorized action remains; do not imply a background service
continues after the session ends.
