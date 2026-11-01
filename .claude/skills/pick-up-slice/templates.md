# Templates for pick-up-slice

Fill the angle brackets. No agent attribution in any of them.

## Pull request body

The owner learns the code by reviewing it, so the body is written to be read before the diff.

~~~markdown
## Walkthrough

<What changed, in the order it is best read, which is rarely the diff's order: usually the data,
then the logic, then the edges. For each part, what it does and why it is shaped that way, in a
few sentences. Name each concept the repository has not had before.>

Closes #<the slice, or the issue the Decisions comment names>

Decisions: <link to the Decisions comment>

## Rests on

- <doc path and passage, or decision in the Decisions comment>: <what in this pull request carries it out>

## Look hardest here

- `<path:line>`: <why a mistake here would be silent or expensive>

## Leaves

- #<leaf> <title>: <short sha>

## Check

<The slice's check, walked on the Mac mini.>

```
$ <command>
<output>
```

Not verified: <what only a browser can show, and how the owner checks it; or "nothing">

## Gate

- Read: <each step run, and its result> (`<command>`)

## Tripwires

<Each doc changed in this pull request, the passage, and why; or "none".>

## Self review

<Each finding and its outcome: fixed (sha), issue proposed (with the draft), or not done and why;
or "no findings".>
~~~

## Summary comment on the slice, after the pull request is open

```markdown
Pull request <link> is ready for review. <One sentence on what it does.> Gate: <all green, or what
failed and why>. Check: <walked, with what was left to the owner>. Not verified: <each state and how
the owner checks it, or "nothing">. Waiting for the owner's review and merge.
```
