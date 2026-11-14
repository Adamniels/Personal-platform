"""Reference vectors for review's FSRS scheduler, from py-fsrs 6.3.3 itself.

Review schedules cards with its own C# port of FSRS-6, src/Review/Scheduling/Fsrs.cs, since no
.NET port is trusted as a dependency. This script runs the reference, py-fsrs 6.3.3 with its
defaults and fuzzing off, over fixed sequences of grades, and prints what it scheduled after each
one as JSON. FsrsTests.EveryReferenceVectorIsReproduced holds the port to every value.

From the repository root:

    uv run --with fsrs==6.3.3 python review/tests/tools/fsrs_vectors.py \
        > review/tests/Review.Tests/Scheduling/fsrs-vectors.json

It is not run in CI: the reference changes only when the version does. Everything in it is fixed,
the pseudo random sequences included, so running it again writes the same file.
"""

from __future__ import annotations

import json
import random
import sys
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from importlib.metadata import version

from fsrs import Card, Rating, Scheduler, State

FSRS_VERSION = "6.3.3"

# Every sequence starts here, or is placed relative to it.
T0 = datetime(2026, 11, 14, 9, 30, 0, tzinfo=UTC)

SCHEDULER = Scheduler(enable_fuzzing=False)

AGAIN, HARD, GOOD, EASY = Rating.Again, Rating.Hard, Rating.Good, Rating.Easy

MINUTE = timedelta(minutes=1)
HOUR = timedelta(hours=1)
DAY = timedelta(days=1)

# A grade and when it is given, relative to the card's due: zero is on time, a positive offset
# late and a negative one early.
Step = tuple[Rating, timedelta]


@dataclass(frozen=True)
class Sequence:
    name: str
    start: Card
    steps: list[Step]


def fresh() -> Card:
    """A card as review makes one: new, Learning at step 0, due when it was made."""
    return Card(card_id=1, due=T0)


def mature(stability: float, difficulty: float, days: int = 0) -> Card:
    """A card in Review whose last grade was `days` before T0, due when its stability says."""
    last = T0 - timedelta(days=days)
    interval = SCHEDULER._next_interval(stability=stability)
    return Card(
        card_id=1,
        state=State.Review,
        step=None,
        stability=stability,
        difficulty=difficulty,
        due=last + timedelta(days=interval),
        last_review=last,
    )


def at(*ratings: Rating, offset: timedelta = timedelta(0)) -> list[Step]:
    return [(rating, offset) for rating in ratings]


def sequences() -> list[Sequence]:
    named = [
        # Each grade from new.
        Sequence("new, again", fresh(), at(AGAIN)),
        Sequence("new, hard", fresh(), at(HARD)),
        Sequence("new, good", fresh(), at(GOOD)),
        Sequence("new, easy", fresh(), at(EASY)),
        # Each grade from learning at step 0, after an again, and at step 1, after a good.
        Sequence("learning step 0, again", fresh(), at(AGAIN, AGAIN)),
        Sequence("learning step 0, hard", fresh(), at(AGAIN, HARD)),
        Sequence("learning step 0, good", fresh(), at(AGAIN, GOOD)),
        Sequence("learning step 0, easy", fresh(), at(AGAIN, EASY)),
        Sequence("learning step 1, again", fresh(), at(GOOD, AGAIN)),
        Sequence("learning step 1, hard", fresh(), at(GOOD, HARD)),
        Sequence("learning step 1, good", fresh(), at(GOOD, GOOD)),
        Sequence("learning step 1, easy", fresh(), at(GOOD, EASY)),
        # Through learning into review, then each grade on time.
        Sequence("review, again", fresh(), at(GOOD, GOOD, AGAIN)),
        Sequence("review, hard", fresh(), at(GOOD, GOOD, HARD)),
        Sequence("review, good", fresh(), at(GOOD, GOOD, GOOD)),
        Sequence("review, easy", fresh(), at(GOOD, GOOD, EASY)),
        # A lapse, then each grade from relearning.
        Sequence("relearning, again", fresh(), at(EASY, AGAIN, AGAIN)),
        Sequence("relearning, hard", fresh(), at(EASY, AGAIN, HARD)),
        Sequence("relearning, good", fresh(), at(EASY, AGAIN, GOOD)),
        Sequence("relearning, easy", fresh(), at(EASY, AGAIN, EASY)),
        # Learning graded a day or more after its step was due: long term stability.
        Sequence(
            "learning, late by days",
            fresh(),
            [(GOOD, timedelta(0)), (GOOD, 3 * DAY), (HARD, 2 * DAY)],
        ),
        Sequence(
            "relearning, late by days",
            fresh(),
            [(EASY, timedelta(0)), (AGAIN, timedelta(0)), (GOOD, 5 * DAY + HOUR)],
        ),
        # Review graded early, the same day as the last grade: short term stability.
        Sequence(
            "review, early on the same day",
            fresh(),
            [(EASY, timedelta(0)), (GOOD, -(8 * DAY) + 2 * HOUR), (EASY, -3 * HOUR)],
        ),
        Sequence(
            "review, early by days",
            fresh(),
            [
                (GOOD, timedelta(0)),
                (GOOD, timedelta(0)),
                (GOOD, -DAY),
                (HARD, -DAY - HOUR),
            ],
        ),
        Sequence(
            "review, late by weeks",
            fresh(),
            [
                (GOOD, timedelta(0)),
                (GOOD, timedelta(0)),
                (GOOD, 21 * DAY),
                (AGAIN, 60 * DAY),
            ],
        ),
        Sequence(
            "learning, early within the step",
            fresh(),
            [(GOOD, timedelta(0)), (GOOD, -5 * MINUTE), (AGAIN, -30 * MINUTE)],
        ),
        # A lapse of a mature card, on time and late.
        Sequence(
            "mature, lapse on time", mature(180.0, 4.2, days=170), at(AGAIN, GOOD, GOOD)
        ),
        Sequence(
            "mature, lapse late",
            mature(365.0, 6.8, days=300),
            [
                (AGAIN, 90 * DAY),
                (HARD, timedelta(0)),
                (GOOD, 2 * DAY),
                (EASY, timedelta(0)),
            ],
        ),
        Sequence(
            "mature, each grade on time",
            mature(42.0, 9.5, days=40),
            at(HARD, GOOD, EASY, HARD),
        ),
        # Toward the extremes of difficulty.
        Sequence("again forever", fresh(), at(*([AGAIN] * 12))),
        Sequence("easy forever", fresh(), at(*([EASY] * 12))),
        Sequence("hard forever", fresh(), at(*([HARD] * 12))),
        # Reaching the maximum interval, 36,500 days, and staying there.
        Sequence(
            "the maximum interval",
            mature(20000.0, 1.0, days=19000),
            at(EASY, EASY, EASY, GOOD),
        ),
    ]

    # Pseudo random walks, from a fixed seed, so the file is the same on every run.
    walker = random.Random(236)  # fixed, so every run writes the same file
    offsets = [timedelta(0), -2 * HOUR, 30 * MINUTE, 2 * DAY, -3 * DAY, 11 * DAY]
    walks = []
    for index in range(12):
        steps = [
            (walker.choice([AGAIN, HARD, GOOD, EASY]), walker.choice(offsets))
            for _ in range(walker.randint(1, 12))
        ]
        walks.append(Sequence(f"walk {index}", fresh(), steps))

    return named + walks


def iso(moment: datetime | None) -> str | None:
    return None if moment is None else moment.isoformat().replace("+00:00", "Z")


def state(card: Card, *, new: bool = False) -> dict[str, object]:
    return {
        "state": "new" if new else card.state.name.lower(),
        "step": card.step,
        "stability": card.stability,
        "difficulty": card.difficulty,
        "due": iso(card.due),
        "last_review": iso(card.last_review),
    }


def run(sequence: Sequence) -> dict[str, object]:
    card = sequence.start
    is_new = card.stability is None
    grades = []
    for rating, offset in sequence.steps:
        # Never before the last grade: a grade earlier than the one before it cannot happen.
        when = card.due + offset
        if card.last_review is not None:
            when = max(when, card.last_review + MINUTE)
        card, _ = SCHEDULER.review_card(card, rating, review_datetime=when)
        grades.append({"rating": int(rating), "at": iso(when), "after": state(card)})
    return {
        "name": sequence.name,
        "start": state(sequence.start, new=is_new),
        "grades": grades,
    }


def main() -> None:
    installed = version("fsrs")
    if installed != FSRS_VERSION:
        sys.exit(f"py-fsrs {FSRS_VERSION} is the reference, found {installed}")
    document = {
        "reference": f"py-fsrs {FSRS_VERSION}, default parameters, fuzzing off",
        "generator": "review/tests/tools/fsrs_vectors.py",
        "sequences": [run(sequence) for sequence in sequences()],
    }
    json.dump(document, sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
