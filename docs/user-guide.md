# Using Bujo-AI

Bujo-AI is a personal bullet journal for getting thoughts out of your head and
deciding which ones deserve your attention. Capture now, plan only as precisely
as you need, and review when you're ready. Moving an item preserves its history,
so changing your mind doesn't erase the original plan. The web app and a
[compatible MCP assistant](mcp.md) work with the same journal.

## Get started in the web app

1. Open the Bujo-AI URL from your deployment owner and sign in with your approved
   Microsoft work or school account. There is no public signup.
2. Open **Capture**, name an item (for example, *Book a bike service*), choose
   **Task** for something to do or **Thought** for a reflection to keep, and select
   **Capture item**. Task is the default. Add a description or tags if useful.
   With no destination or planned period selected, the item goes to the **Future
   Log**, unplanned.
3. Open **Lists → Future Log** to find it again, or create a custom or weekly list
   in **Lists**. Open an item to edit its details, set a planned period, or move
   it. Use **Complete item** for work that's done and **Abandon item** when you no
   longer intend to do it. Open an item from an old list to see its current
   destination and occurrence history. Thoughts remain readable but cannot be
   completed, abandoned, or reopened.

## Make a daily and weekly habit

- **Today** resurfaces unfinished task plans, including overdue items, today's items,
  and this week's plans. **Upcoming** lets you look across a future date range.
  These are views you open, not reminders or notifications.
- Today also offers two keyboard-accessible actions on an actionable overdue
  row:
  - **Plan for today · YYYY-MM-DD** changes the planned period to the local date
    shown on Today.
  - **Plan for this week · YYYY-Www** changes the planned period to the ISO week
    shown on Today.
  The target is displayed on each button before you activate it. Nothing changes
  until you choose a button. Rescheduling changes only the planned period; it
  doesn’t move the item or change its occurrence.
  After a successful change, Today refreshes the row and confirms the new plan.
  Focus returns to the item’s detail link, which remains available for other
  edits.
  If the item changed since Today loaded, the action is rejected as stale. Use
  **Refresh journal** to review its current state before trying again. A new
  planned period can also conflict with the week of the item’s destination list;
  Today shows a conflict message without moving the item or changing the selected
  period. Resolve that conflict with an explicit edit if needed.
- A **planned period** is an intention, not a deadline. Choose a day, week, month,
  quarter, or year, or leave the item unplanned. The period is separate from the
  list: moving an item doesn't reschedule it, and changing its period doesn't
  move it.
- When a list is winding down, select **Start review** on the list page. Look at
  unfinished active items separately from items already moved or resolved. You
  can leave items where they are by keeping the list open. To submit a review
  proposal, choose whether to move, complete, or abandon each unfinished task.
  Inspect the proposal before approving and applying it. A list can close only
  after its unfinished tasks are resolved; Thoughts do not block closure. The
  Future Log stays open.

## Conditional follow-ups

1. On the start page, find **Follow-ups due** and select **Capture a follow-up**.
   Enter **Title**, **Condition** (for example, “if I haven't heard back”),
   **Due date and time**, and explicitly select **Manual** under **Evidence
   source**. Optionally enter a **Journal item ID** for an existing item.
   Select **Save follow-up**; use **Review saved follow-up** to open it.
2. The due time is entered and displayed in your browser's time zone and stored
   in UTC. If a local time does not exist or occurs twice because of a clock
   change, choose another time. Pending follow-ups appear in **Follow-ups due**
   when due; select the title to review one.
3. Under **Record or correct manual evidence**, choose **Reply seen** or **No
   reply seen**. For a no-reply outcome, enter what you checked in **Checked
   scope**, such as “inbox and spam, 3 Dec”, and select **Record evidence**.
   With no evidence or no scope for a no-reply observation, the outcome is
   **Evidence unavailable**, not proof that no reply exists. A no-reply outcome
   applies only to the recorded scope.
4. Record another observation to correct the outcome. **Evidence history**
   keeps prior entries; the latest entry determines the outcome. Choose
   **Defer** with a new due time or **Dismiss follow-up** explicitly. Under
   **Draft for review**, write or paste text and select **Save draft**; it is
   never sent. If another edit changed the draft, copy your unsaved text before
   choosing **Load latest draft and discard my edit**.

This is a pull-based review, not a scheduler or closed-app notification. The
feature does not read a live mailbox, contact anyone, or send messages
automatically, and does not generate AI drafts. Live mailbox evidence needs a
separately approved integration and privacy/reliability verification.

For repeatable assistant routines, see the versioned
[Bujo assistant skills](../assistants/README.md). Manual equivalents are to open
**Today** for a daily review, use **Capture** for one item, and select **Start
review** on a weekly list. No client is verified for unattended scheduled Bujo
prompts; use these in-app flows until a specific client is verified.

## Save reference material

Use **Knowledge** for notes, concepts, and quotes you want to find again, rather
than things you intend to do. Select **Capture a knowledge entry** and save the
original passage, optionally with a title, source reference, and tags. Search
by words, quoted phrases, or tags (all selected tags must match).
Archive an entry to hide it from default search
without deleting it. **Knowledge → Journal backup** exports the journal and
knowledge and follow-ups as a JSON file containing private data. Keep it
somewhere secure. Restoring from a backup replaces current data; export a fresh
backup before restoring an older one. A backup without follow-ups retains
current follow-ups, but restore is rejected if a retained follow-up would point
to an item missing from the backup. See the [backup warning](operations.md#follow-up-backup-and-rollback-warning).

## Plan a family weekend with saved ideas

Save places, activities, or other reference material as **Knowledge** entries,
with useful tags such as `family`, `weekend`, or `activity`. In a connected
remote MCP assistant, ask it to suggest family-weekend activities from your
saved ideas; the `family_weekend_ideas` prompt asks about relevant missing
constraints and returns a few options with their saved-entry sources. If no
saved idea matches, it says so rather than inventing one. The prompt does not
read journal items or access your calendar, and it never books, messages, or
silently saves a plan. Public search is only considered when you explicitly ask
and your client already provides it; the in-app chat does not yet read
Knowledge.

## Choose how to interact

Use the web app for direct edits and review approval. If **Chat** is enabled,
try asking what is planned for today; available actions depend on the configured
provider. Chat history isn't saved by the app. You can also use your own
assistant through [remote MCP](mcp.md) or use its versioned skills if your
deployment and client support the connection. Client compatibility and
scheduled execution remain unverified. If chat or MCP isn't available, use
**Today**, **Capture**, **Lists**, and **Knowledge** directly.

The app is online-first: even if your browser lets you install it, it doesn't
queue offline edits. If the connection drops during a save, reconnect and check
whether the change appears before repeating it.

For the product rationale and planned capabilities, see the
[product concept](concept.md). This guide describes the app in the repository,
not which version is running on any particular deployment.
