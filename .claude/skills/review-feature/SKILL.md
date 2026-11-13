---
name: review-feature
description: 'Use when the owner asks to look at a whole feature after its slices have merged ("review the brain", "/review-feature", "look over what the agents built", "review feature 159"). Runs on the laptop with the owner. Reads everything the feature merged as one piece, reports what a slice by slice review cannot see, and ends, if he wants, with follow up slices. Never starts on its own.'
---

# Review a whole feature

Every slice was reviewed alone, against its own ticket. Nobody has looked at the feature as one
piece: whether its slices fit together, whether a rule held everywhere or only where a test
checked it, whether the docs still match the code, and whether it became what the owner wanted.
That is this skill's job. The owner no longer reads every line that lands, so this is where the
whole is read once.

**It is the owner's to start, and only his.** Nothing in the workflow waits for it, no feature is
blocked by it, and no status says it is due. He runs it when he wants a feature looked at, the way
he runs `/plan-feature` when he wants one planned, and he may skip it. Never suggest it has to
happen, and never start it because a feature's last slice merged.

You judge and report. You write no code, and you create no issue without his yes.

How this fits the workflow is in "Looking at a whole feature" in
`High-level/docs/agent-workflow.md`.

## 0. Start clean

A session that planned the feature is the wrong one to review it: its blind spots are in the plan
itself. If this session planned or built the feature, say so and recommend a fresh one. It is his
call, and if he says go on, go on.

Ask which feature, if he did not say: a feature issue's number, or its name.

## 1. Read the whole feature

All of it, before the first finding. Grep, do not work from memory.

- **What he wanted.** The feature issue's body, which holds his words. And its page in his planning
  notes, when the session has the Notion connector: the Features database on the "Personal
  Platform" page. The page is intent, and it is the measure in step 3.
- **What was planned.** Every slice of the feature, in order, with its Settled and Acceptance:
  `gh api repos/Adamniels/Personal-platform/issues/<n>/sub_issues --jq '.[] | "#\(.number) \(.state) \(.title)"'`,
  then each body.
- **What the agents said about it.** Every pull request that closed a slice. From each body:
  "Decisions the agent made", "Not verified" and "Doc changes". From each review: the verdicts, the
  nits, and "For the owner". Collect these across the feature. A nit nobody fixed and a thing
  nobody verified are both findings waiting to be read together.
- **What was built.** The code as it is on `main` now, for every area the feature touched, and its
  tests. Read the code itself, not only the diffs: a diff shows what one slice changed, and the
  question here is what the whole now is.
- **The docs.** Every doc the feature changed or should have changed, and for any UI
  `design/DESIGN.md`, whose section 0 says what is binding.
- **How it runs,** when it has a surface: the screenshots in the pull requests, and the platform
  itself if he has it running.

For a large feature, read in parallel by area, then check every finding yourself against the code
before it goes in the report. A finding you have not confirmed is not a finding.

Then say back, in a few sentences, what the feature is and what you read, so he knows what the
review covers and what it does not.

## 2. What to look for

What a review of one slice cannot see:

- **Things that do not fit together.** Two slices that each work and disagree: on a name, a shape,
  an error, who owns a rule.
- **The same thing built twice.** A helper, a check or a query written again by a later slice that
  did not find the first.
- **A rule that holds in one place and not another.** Isolation, authentication, validation, the
  design's hard rules. Find every place the rule should apply, not only the ones a test covers.
- **Docs and code that disagree.** In both directions: a doc that describes what was planned and
  not what was built, and code with no doc where the docs say there is one.
- **The agents' decisions, read together.** Each was judged alone against the standard in step 8 of
  `pick-up-slice`. Read them as a set: do they pull in one direction, did one contradict another,
  did several small ones add up to something nobody chose?
- **What was left behind.** Nits the reviewer raised that nobody fixed. Everything under "Not
  verified". Anything a slice put "out of scope" that no later slice picked up.
- **Tests that prove less than they say.** A named acceptance test that passes without checking
  the guarantee, and a guarantee with no test at all.
- **Whether it became what he wanted.** Against his words in the feature issue and his planning
  page: what is missing, what is there that he did not ask for, and what works but would be
  awkward to use.

Do not repeat what the slice reviews already settled, and do not invent findings to look thorough.
A feature that holds together gets a short report that says so.

## 3. Report

One report, in the conversation, written for him to act on:

- **What it is, in two sentences,** and whether it holds together.
- **Findings, the most serious first.** Each with what is wrong, where, as `path:line`, why it
  matters, and how big the fix is. Say plainly how sure you are: confirmed in the code, or
  something you suspect and could not confirm.
- **Sorted into three:** worth fixing now, worth a ticket for later, and noticed but fine to leave.
  Recommend, do not only list.
- **What it says about how the work was planned and built.** If the same kind of problem shows up
  more than once, the fix belongs in planning, in the agents' procedure or in a doc, not only in
  the code. Name it.

Then go through it with him, **one finding at a time**, most serious first: explain, recommend,
and wait. He decides what becomes work.

## 4. Turn what he agrees to into slices

Only what he said yes to, and only if he wants tickets at all.

- Follow up work is slices, written the way `/plan-feature` writes them: the seven headings of its
  step 4, every acceptance criterion a named test, Settled complete enough that an agent never
  has to ask. Small findings that belong together go in one slice.
- They go under the same feature issue while it is open, and under a new one, "Follow up: <feature>",
  when it is closed.
- Created from the owner's account, shown as a dry run first, run on his yes, verified with a read,
  as step 5 of `/plan-feature` says. Check `gh api user --jq .login` prints `Adamniels` first.
- A change to how features are planned or built, the last point of the report, is not a slice. It
  is a change to a skill or a doc, proposed to him and made as its own pull request on his yes.

## Never

- Start because a feature finished, or tell him a review is due.
- Change code, or fix what you find. The findings are his to decide on.
- Create or edit an issue without his yes for that issue.
- Report a finding you have not confirmed in the code as if you had.
- Review a feature this session planned without saying so first.
