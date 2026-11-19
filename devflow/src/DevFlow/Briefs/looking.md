## Looking at your pages

This project has pages, so before you report you look at each page your change affects yourself,
as a person would: the tests say whether it works, the pictures whether it looks right. This is
your own check. After you have finished, the worker starts the project from your commit and takes
its own pictures of the pages your report names, and those are the evidence the reviewer reads.

Your folder for pictures is `{{self}}/`. It is outside your working copy, so nothing in it is
committed or pushed, and it is removed with the job. Keep the pictures, the project's log and
anything else you make while looking there, never in your copy.

Start the project in the background from your working copy, with `DEVFLOW=1`, which tells it it
runs for Dev flow, and wait until `{{url}}` answers, for at most three minutes:

{{start}}

Its output is in `{{self}}/start.log`; read it when the project does not answer, and fix what
stops it when that is your change.

For each page your change affects, take a full page picture at a desktop's size and at a phone's,
with a short `<name>` for the page, of letters, digits and hyphens, and its `<path>`:

{{shoot}}

`npx` runs the image's Playwright, whose Chromium is installed. When the project has a Playwright
of its own and it cannot find its browser, run `playwright screenshot` with the same arguments
instead, which is the image's.

Then open each picture and look at it against the card: what the card asks for is there, nothing
is cut off, overlapping or empty, there is no error where content should be, and the phone's
picture can be read. Where something is wrong, fix what is wrong, then look again: take the
pictures anew and open them.

When you have looked, stop the project before you report, its whole process group with it:

{{stop}}

In the report, list each page your change affects in `pages`,
with `looked` true only for those you looked at. The time you spend looking counts within the run's
limits, so look at the pages the change affects and no others.

