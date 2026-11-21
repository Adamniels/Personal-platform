# Dev flow redesign

The pictures here are the design for "Dev flow: the redesign", planned with the owner on 2026-11-21. Each belongs to one slice of that feature, which links it under Context.

**How to read them.** A slice's text is binding; where it and a picture differ, the slice is right. A picture fixes the layout, the order of what is on a page, the hierarchy and which components are used. Every title, number, name, date and commit in it is sample data and never a fact about Dev flow. Colours, sizes, radii and type come only from `design/tokens.css` and the rules in `design/DESIGN.md`, never from a picture. The pictures show the plan, not the result: what was built is in each pull request's screenshots.

| Picture | What it shows | Slice |
|---|---|---|
| `waiting-desktop.png` | Waiting for you as a queue and the chosen item, here a decision to look at | Waiting for you as a queue and one item; An item's detail, by kind |
| `waiting-phone-queue.png` | The queue on phone | Waiting for you as a queue and one item |
| `waiting-phone-item.png` | A pull request on phone: its check, review and decisions as tiles, and its actions above the tab bar | Waiting for you as a queue and one item; An item's detail, by kind, for the tiles |
| `waiting-questions.png` | A card's questions in the detail | An item's detail, by kind |
| `projects-tab.png` | The Projects tab as a dashboard | The Projects tab as a dashboard |
| `project-page.png` | A project's page, cards first | A project's page: cards first |
| `project-setting-up.png` | A project while setting up | A project setting up |
| `project-settings.png` | A project's settings page | A project's settings page |
| `card-page.png` | A card's page waiting for the owner | A card's page: report, timeline, at a glance |
| `feature-page.png` | A feature's page ready to merge | A feature's page: merge and decisions first |

The pictures were drawn before "The shell: rail and account at the window's edges". Where they place the rail, the account pill or the content column differently, that slice is right: the rail and the pill at the window's edges, the content centred beside the rail.
