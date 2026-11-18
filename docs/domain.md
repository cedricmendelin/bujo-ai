# Journal domain semantics

This specification records the defaults approved in
[issue #3, implementation plan v1](https://github.com/cedricmendelin/bujo-ai/issues/3#issuecomment-6883079667).
The domain implementation depends on the approved technology baseline in
[issue #2](https://github.com/cedricmendelin/bujo-ai/issues/2).
Knowledge entries have a separate contract and are not journal occurrences.

## Identity and lists

- An **item** is the canonical task or thought: it has an identity, name,
  description, customizable tags, a planned period, and a reference to its
  current occurrence. It has no global status. Tags are free-form, normalized,
  and unique per item without regard to case. Domain inputs have length limits.
- A **list** has an identity, name, `FutureLog`, `Weekly`, or `Custom` kind,
  `Open` or `Closed` state, optional goal text, and an optional calendar week key
  for weekly lists. There is exactly one ongoing Future Log; it cannot be closed.
  List names are globally unique without regard to case. Goals are list text,
  not a goal hierarchy.
- Multiple weekly lists may share an ISO year/week key; uniqueness is by list
  name, not by week key.
- An **occurrence** is one visit by an item to a list. It has its own identity,
  local state, sequence number, UTC timestamps, and previous/next occurrence
  links for moves. Only an `Open` occurrence is active. The current occurrence
  is the tail of the item's chain even when it is `Done` or `Abandoned`; in
  those states the item has no active occurrence.

Captures without an explicit list create an occurrence in the Future Log.
Unless a planned period is supplied, the item is `Unplanned`. Item text and
tags remain canonical: historical views show their current values, not snapshots.
Auditing text edits is outside the MVP.

## Occurrence transitions

`Planned` is not an occurrence state. Planning into a weekly list is a move;
the item's planned period is independent of its list.

```text
                  move
Open --------------------------------> Moved -> new Open occurrence
  |                                        (on another list)
  +--> Done ---------+
  |                  +--> Open (explicit reopen)
  +--> Abandoned ----+
```

| Current state | Done | Abandon | Move | Reopen |
| --- | --- | --- | --- | --- |
| `Open` | `Done` | `Abandoned` | `Moved`, with a new `Open` destination | Reject |
| `Done` | Reject | Reject | Reject | `Open`, subject to conditions below |
| `Abandoned` | Reject | Reject | Reject | `Open`, subject to conditions below |
| `Moved` | Reject | Reject | Reject | Reject |

`Done` and `Abandoned` are terminal for normal transitions. They may be
explicitly reopened only when the occurrence is still the item's current
occurrence and its list is open. Reopening is recorded in history. A `Moved`
occurrence is never reopened; returning to an earlier list is another move that
creates a new occurrence rather than reusing the old one.

A move requires the current `Open` occurrence and an open destination list
different from its current list. It atomically marks the source `Moved`, creates
the new `Open` occurrence, links both directions, updates the item's current
reference, and appends an ordered history entry. Each occurrence has at most
one predecessor and successor, with increasing sequence numbers: the history is
a linear chain, not a cycle or a set of active branches.

To resolve any occurrence, follow its next links to the tail and report that
occurrence's list and current local state. For example, after MVP → Week 47 →
Week 48 → `Done`, both MVP and Week 47 resolve to Week 48 and `Done` while their
own local states remain `Moved`.

## Atomicity and list closure

Each move carries a client-supplied operation id and expected current-occurrence
id. Retrying the same operation returns its original result without another
move; a different operation with a stale expected occurrence fails with a
conflict. Move history, links, current reference, and the operation result must
commit atomically. Concurrent moves cannot create multiple active destinations:
the repository port must guarantee at most one `Open` occurrence per item and
an optimistic version check. The later SQL persistence implementation must
enforce this with a unique filtered index; the domain module does not create it.

Closing a list is rejected with its unresolved `Open` occurrences. Move,
complete, or abandon each explicitly before closing; historical `Moved` links
do not block closure. A closed list accepts no new occurrences, and an
occurrence cannot be reopened there. A closed list can itself be explicitly
reopened. Closure and concurrent writes must be coordinated so that a closed
list cannot acquire an `Open` occurrence.

## Manual list review proposals

A list review presents unfinished `Open` occurrences separately from historical
`Moved` links, which include their resolved final destination and state.
`Done` and `Abandoned` occurrences appear as resolved history. Moved links do
not block closure; every unfinished occurrence must have an explicit move,
done, or abandon disposition before a closure proposal can be created.

Bulk, ceremony-generated, destructive, and disposition-plus-closure changes
are immutable proposals containing exact item/occurrence IDs, expected item and
list versions, destinations, deterministic operation IDs, a 24-hour expiry, and
a server-computed SHA-256 digest. The creator approves that digest only from
their approved interactive PWA session. MCP can create, inspect, cancel, reject,
and apply proposals, but cannot approve them or claim approval in a tool
argument. Application verifies the single-use approval and all expected
versions and applies the changes plus requested closure in one repository
transaction. A stale proposal is marked conflicted and must be reviewed and
approved again. Cancelled or rejected proposals do not change journal data.

Reopening a closed list is a direct explicit action. It changes only the list
state; closed-list occurrences retain their prior states and must be explicitly
reopened or otherwise acted on before they become active again. New
occurrences may be created only after the list is reopened.

## Planned periods and calendar

An item has one planned period: `Day`, `Week`, `Month`, `Quarter`, `Year`, or
`Unplanned`. A planned period uses a calendar anchor appropriate to its
granularity, not an instant; `Unplanned` has no anchor. There are no separate
deadline or reminder fields. Changing the period never changes the list,
occurrence state, links, or move history.

Weeks use ISO 8601: Monday is the first day and a week is identified by its ISO
week-year and week number, which can differ from the calendar year. Month,
quarter, and year are calendar values. The user supplies an IANA time zone;
derive “today” from the current instant in that zone using an injected clock
and zone. Persist event instants in UTC, not in local time.

When a destination weekly list has a week key, a day planned outside that week,
or a planned week different from it, produces a non-blocking `PeriodConflict`
for the caller when an occurrence is created or moved there. The operation
remains allowed and neither the period nor the list is rewritten. Resolving
the conflict requires an explicit user decision. The plan does not define a
week-conflict rule for a weekly list without a week key, or for `Month`,
`Quarter`, `Year`, or `Unplanned`.

## Daily-start and upcoming queries

The caller supplies an IANA time-zone ID for every query; the service has no
server-local or UTC default. “Today” is the instant from the injected clock
converted to that zone. Weeks start Monday and use ISO week-year and week
number. Days, months, calendar quarters (January–March, April–June, July–
September, October–December), and years use local calendar-date ranges,
inclusive of both endpoints. Event instants remain UTC; daylight-saving
transitions do not change period membership.

Daily start considers only items whose current occurrence is `Open`. It groups
them in this order:

1. `Overdue`: a planned day before today, a planned week ending before the
   current ISO week's Monday, or a planned month, quarter, or year whose final
   date is before today. Each reason identifies the planned-period precision.
2. `Today`: a planned day equal to today.
3. `ThisWeek`: a planned week equal to the current ISO week, with no specific
   day.

A month, quarter, or year containing today is not overdue and is not included
in daily start. Unplanned items, future planned days, and other not-yet-ended
broader periods are excluded from daily start. Weekly-list conflicts remain
informational: the item is included in its applicable group with a conflict
flag, and neither its planned period nor its list is changed.

Each result identifies the canonical item and its current destination. Resolve
the current occurrence through the existing move chain; never return moved
historical occurrences as separate actionable entries. Done and abandoned
current occurrences are excluded without removing history. Missed plans retain
their original dates and are presented for an explicit user decision.

Upcoming planning queries include open items with future planned periods within
a bounded local-date horizon (14 days by default, 90 maximum), grouped by
period and returned with a bounded page size and a truncation indicator. A
separate paged Future Log query returns its open items, including unplanned
items. Daily start may report Future Log/unplanned counts, but does not expand
the backlog into actionable entries. All query ordering and group ordering are
deterministic. Invalid or missing time zones are explicit errors, not an empty
successful result.

## Implementation acceptance

Once issue #2's ADR is approved, implement the persistence-free domain and
repository port described in plan v1. Tests must cover the complete transition
table and reopen conditions, canonical edits in historical views, returning to a
prior list, linear history and period independence, multi-hop resolution,
retried and concurrent moves, and list closure with unresolved occurrences.
Also test goal text and tag normalization, ISO week-year boundaries including
week 53, daylight-saving transitions, local today across a UTC day boundary,
and non-mutating weekly period conflicts.
