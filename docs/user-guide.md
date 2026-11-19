# Using Bujo-AI

Bujo-AI is a personal bullet journal for getting thoughts out of your head and
deciding which ones deserve your attention. Capture now, plan only as precisely
as you need, and review when you're ready. Moving an item preserves its history,
so changing your mind doesn't erase the original plan. The web app and a
[compatible MCP assistant](mcp.md) work with the same journal.

## Get started in the web app

1. Open the Bujo-AI URL from your deployment owner and sign in with your approved
   Microsoft work or school account. There is no public signup.
2. Open **Capture**, name an item (for example, *Book a bike service*), and select
   **Capture item**. Add a description or tags if useful. With no destination or
   planned period selected, the item goes to the **Future Log**, unplanned.
3. Open **Lists → Future Log** to find it again, or create a custom or weekly list
   in **Lists**. Open an item to edit its details, set a planned period, or move
   it. Use **Complete item** for work that's done and **Abandon item** when you no
   longer intend to do it. Open an item from an old list to see its current
   destination and occurrence history.

## Make a daily and weekly habit

- **Today** resurfaces unfinished plans, including overdue items, today's items,
  and this week's plans. **Upcoming** lets you look across a future date range.
  These are views you open, not reminders or notifications.
- A **planned period** is an intention, not a deadline. Choose a day, week, month,
  quarter, or year, or leave the item unplanned. The period is separate from the
  list: moving an item doesn't reschedule it, and changing its period doesn't
  move it.
- When a list is winding down, select **Start review** on the list page. Look at
  unfinished active items separately from items already moved or resolved. You
  can leave items where they are by keeping the list open. To submit a review
  proposal, choose whether to move, complete, or abandon each unfinished item.
  Inspect the proposal before approving and applying it. A list can close only
  after its unfinished items are resolved; the Future Log stays open.

## Save reference material

Use **Knowledge** for notes, concepts, and quotes you want to find again, rather
than things you intend to do. Select **Capture a knowledge entry** and save the
original passage, optionally with a title, source reference, and tags. Search
by words, quoted phrases, or tags (all selected tags must match).
Archive an entry to hide it from default search
without deleting it. **Knowledge → Journal backup** exports the journal and
knowledge as a JSON file containing private data. Keep it somewhere secure.
Restoring from a backup replaces current data; export a fresh backup before
restoring an older one.

## Choose how to interact

Use the web app for direct edits and review approval. If **Chat** is enabled,
try asking what is planned for today; available actions depend on the configured
provider. Chat history isn't saved by the app. You can also use your own
assistant through [remote MCP](mcp.md) if your deployment and client support
the connection. If chat or MCP isn't available, use **Today**, **Capture**,
**Lists**, and **Knowledge** directly.

The app is online-first: even if your browser lets you install it, it doesn't
queue offline edits. If the connection drops during a save, reconnect and check
whether the change appears before repeating it.

For the product rationale and planned capabilities, see the
[product concept](concept.md). This guide describes the app in the repository,
not which version is running on any particular deployment.
