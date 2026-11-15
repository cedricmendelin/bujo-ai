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
  Goals are list text, not a goal hierarchy.
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

## Implementation acceptance

Once issue #2's ADR is approved, implement the persistence-free domain and
repository port described in plan v1. Tests must cover the complete transition
table and reopen conditions, canonical edits in historical views, returning to a
prior list, linear history and period independence, multi-hop resolution,
retried and concurrent moves, and list closure with unresolved occurrences.
Also test goal text and tag normalization, ISO week-year boundaries including
week 53, daylight-saving transitions, local today across a UTC day boundary,
and non-mutating weekly period conflicts.
