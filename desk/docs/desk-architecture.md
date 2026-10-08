# Desk

Status: idea. The shape is described, almost nothing is modelled, and nothing is built.

Last updated: 2026 10 08

Desk is where the owner sits down to work through the day: a calendar to plan time in, sessions to
do the work in, projects to organise it, and a review space to make it stick. This document holds
what has been decided, what has deliberately been left open, and the evidence the ideas came from.
It is the starting point for when desk gets picked up, not a plan for building it.

How firmly to read it is in `docs/README.md`. Why desk is the first feature after the core is in
"Candidate features" in `High-level/docs/platform-architecture.md`. The original sketch is
`sessions-make-it-stick.jpg` beside this file.

---

## The problem

The work gets done in a session and then everything is left there. Questions that came up, things
worth remembering and what to do next are never picked up again.

The Notion school system was built to fix this, and it shows why it did not. Of the 41 scheduled
sessions that had already taken place by 2026 10 08, 6 were primed, 5 were consolidated and none
was ever reviewed. A typical page has around fifteen prompt headings with one line filled in. The
design was thorough and too heavy to use: the checkboxes went unused, priming wanted to be a few
questions answered in one flowing text rather than a form, and consolidation did not match how the
work actually happened. The review half, the part meant to make things stick, never ran.

Two things follow, and they are the test to hold any later design against:

- **Capturing costs almost nothing.** Every field is optional, and a session with one question in
  it is a success.
- **Review comes to the owner.** A queue that has to be remembered and opened is what failed. How
  reviews arrive is open, see Review below, but waiting to be visited is ruled out.

## What desk is, and what it is not

**One feature.** Projects, sessions, the calendar view and review reference each other's records
constantly: a session belongs to a project, a project lists its sessions, a kanban card links to
the sessions spent on it, and review reads questions written inside sessions. Split across
features, every one of those links would cross a feature boundary, and
`High-level/docs/feature-contract.md` rules out features calling each other. Kept together they
are joins inside one domain.

**What joins desk is decided by data coupling, not by theme.** Something belongs here when it has
to reference projects or sessions. Habits, books and the RSVP reader do not, so they are not desk
however much they are part of daily life. A name like "daily life" was rejected for that reason:
a theme invites everything, and the feature grows into the monolith the platform exists to avoid.

**Desk is not the home page.** The core's front page is home, where everything is summarised.
Desk is what gets opened to sit down and work through the day. Like any feature it may expose
display data to the front page, such as today's sessions and reviews due.

**Sessions are the first part of desk to be built**, since they are what gets used every day.

## Projects

Projects hold work that runs over time, with a kanban, notes and files.

**School is a project, not a model of its own.** With a year and a half of studies left, a
dedicated school structure would be built for a single use. Coursework, meaning assignments, labs
and exams with deadlines, is a kanban inside the School project.

## Sessions

A session is a block of time where work happens. Clicking a block in the calendar opens its
session if one exists, or creates one. The calendar is one way to reach a session, not the only
one, see Views.

**Templates, starting with one.** Learning something new and working on something turned out to
need the same fields, so they are one standard template. Further templates are added in code when
a real need shows up.

A session holds:

- **Planning.** What to accomplish and what the goal is. Filled in whenever suits, a week ahead or
  in the first minutes of the session, since what matters is that the place exists rather than
  when it is used. The exact fields are open.
- **Questions and answers**, in the style of Cornell notes. Questions that come up during the
  work, each with its answer, stored so they can be reviewed later. This is the main thing a
  session is for. A repeat session on the same material uses the same place, so new questions
  land beside the old ones.
- **General notes.**
- **Reflection**, at the bottom. Normally the last five to ten minutes, but when is up to the
  owner.

A session also carries fields that place it. They are described here, not modelled:

- **Project**, a relation to a project such as School.
- **Subject**, which takes the place of a course.
- **Category**, which takes the place of a section.

Whether project, subject and category form a hierarchy or are independent tags is open. Subject is
obvious inside School and unclear for a project like the platform itself, and that difference is
what should settle it.

How a session relates to a coursework card, such as a session spent on Assignment 3, is open.

## Calendar

Desk starts on top of Google Calendar rather than being a calendar of its own. A calendar of its
own means recurring events, time zones and phone sync, which is a project in itself and not where
the value is. The condition is that Google supports everything desk needs to do with events, and
nothing found so far says it does not.

**Google owns the time, the session owns the content, and they link by id.** Desk reads and writes
Google events. Every event has a stable id and recurring instances have their own, and an event
can carry hidden key value pairs in `extendedProperties.private`, so the session id can live on
the event and the link works in both directions. A session can exist without an event, so leaving
Google later costs nothing.

**Open:** desk holds a Google OAuth refresh token for the owner, a long lived credential. Where it
is stored and how it is protected is undecided.

## Views

The calendar is a view over sessions. Beside it sits a list of sessions, filtered by project,
subject, category, date and so on. Which views exist beyond these two is open.

## Review

A space of its own for working through the questions written in sessions, closer to flashcards
than to reading notes back. The owner chooses what to work on: a particular day, one subject,
particular sessions, and other ways still to be found.

The rules are all open: the scheduling, whether review is tracked per question or per session, and
how due reviews reach the owner. The notification service in
`High-level/docs/feature-contract.md` is the likely answer to the last one.

## Open, and deliberately later

- **What desk sends the brain.** Decided when desk is designed in earnest, against the filter in
  `Brain/docs/knowledge-model.md`.
- **The first version.** What the smallest useful desk is gets cut when the work is planned,
  following `High-level/docs/how-we-build.md`.
- **The overlap with learning.** "Learning new topics" is a separate candidate feature and desk is
  not it, but both have sessions and both quiz, and `High-level/docs/feature-contract.md` already
  says a learning session belongs to learning. When learning is planned, decide whether it reuses
  desk's sessions and review or keeps its own, and settle what the word session means across the
  platform. The open note in `Brain/docs/trust-model.md` about what a session is belongs to the
  same question.
