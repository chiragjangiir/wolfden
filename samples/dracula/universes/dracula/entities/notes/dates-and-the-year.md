---
id: 01M4TF5F49JG7KM3CXSCQBZPM0
type: note
name: Dates and the year
aliases: [Calendar]
status: canon
---

# Dates and the year

How this den writes in-world dates. Every journal entry, letter, and
telegram in the novel carries a day and a month, but the novel never
states the year.

## Sort keys
<!-- wolfden: pin=true -->
Every date in this den with a place on the timeline is written as a
mapping, with the date as the novel gives it in `text` and a sort key of
three numbers:

```yaml
when: { text: "3 May", sort: [0, 5, 3] }
```

The numbers are **year, month, day**, where the year counts from the year
of the main action, so `0` is the year Harker travels to Transylvania. The
closing Note, "seven years" later, is year `7`. A later work set in the
same world could carry on counting.

A sort key of month and day alone (`[5, 3]`) was tried and dropped: the
Note's year would then sort as a month, between June and August.

Times of day stay in the text ("6 November, at sunset"). The novel seldom
gives a clock time, and the day is enough to order the events.

## The year
<!-- wolfden: status=undecided -->
The novel never names the year of the main action. The question is
banked here instead of being settled in every date.

### 1893
<!-- wolfden: status=candidate -->
Stoker's surviving working notes are often read as setting the story in
1893, and readers who check the weekdays the text mentions against a
calendar tend to agree. If adopted, every sort key's `0` would become
`1893` and the Note's `7` would become `1900`.

### Leave it unstated
<!-- wolfden: status=candidate -->
Keep the relative year. The book reads as "a few years ago" to its first
readers, and a sort key that only orders events is all the timeline
needs.
