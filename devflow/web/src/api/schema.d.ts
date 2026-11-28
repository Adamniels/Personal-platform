// Generated from contracts/devflow/openapi.json by tools/generate-schema.mjs, with
// `npm run generate:api`. Do not edit: src/api/schema.test.ts fails when it is stale.

export interface paths {
    "/devflow/api/api-key": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's Anthropic API key
         * @description Whether one is set and when it was given. Never the key or any part of it. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getApiKey"];
        /**
         * Give Dev flow the caller's Anthropic API key
         * @description The key from the Claude Console, sealed in the vault and replacing any before it, given now. The space around it is dropped. Answers the status it leaves, never the key. 422 naming key for anything that does not look like an API key, sk-ant-api followed by more visible characters, a setup token among them, and nothing is stored; Dev flow does not ask Anthropic whether it works. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["setApiKey"];
        post?: never;
        /**
         * Remove the caller's Anthropic API key
         * @description Forgets the key and its sealed secret. 204, whether or not there was one. 409, "These projects run on it: <names>", while any of the caller's projects runs on it, and nothing is removed. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        delete: operations["removeApiKey"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/attention": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Whether something needs the caller, for the nav bar
         * @description Read from what Dev flow stored and never from GitHub. attention is true when the caller has a pull request, waiting or stopped, that is not sent back, a stopped project, a card of Dev flow's whose pull request waits, a card of Dev flow's refused as too thin, a card of Dev flow's waiting for the caller's answers to its questions, a feature of Dev flow's ready for review or waiting for the caller, a GitHub link that has expired, or a Claude login due for renewal within a month or past it, and false otherwise. The nav bar asks it with the session's cookie. Without a valid token, 401 with an empty body.
         */
        get: operations["getAttention"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/cards/{id}/answers": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Answer a card's questions, as the caller, and queue it
         * @description Sends the caller's answers to the questions of the reading reading_id of a card of Dev flow's own that needs answers: answers holds one for each question, in the reading's order, each either accept true, the reader's recommendation, or text, the caller's own, 1 to 4,000 characters once trimmed, never both. They are posted as one comment on the card's issue, as the caller's linked GitHub account and never as the app, listing each question with its answer and (recommended) where it was accepted; the comment's id is kept, so it never reads the card again, the answers are kept with the reading, and the card is queued, its build's brief carrying them under "Answers to its questions". A comment of the caller's on the issue that is exactly the one to post, left by a send whose answer was lost, is kept instead of posting it again. In order: 404 for a card the caller does not have; 422 without reading_id or answers; 409 when the card does not need answers; 409 when reading_id is not its newest reading; 422 when answers does not hold exactly one valid answer for each question; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted and kept. 409 when the card moved on while GitHub answered, the comment posted; 409 when GitHub refuses, with its reason; 503 when GitHub did not answer, nothing kept, and sending the same answers again finds a comment it took. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["answerCard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/claude-login": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's Claude login
         * @description Whether one is set, when it was given and renew_by, a year after; state is none, ok, due_soon from 31 days before renew_by, or expired from renew_by on. Never the token or any part of it. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getClaudeLogin"];
        /**
         * Give Dev flow the caller's Claude login
         * @description The token from claude setup-token, sealed in the vault and replacing any before it, given now, so its year starts again. The space around it is dropped. Answers the status it leaves, never the token. 422 for anything that does not look like a setup token, sk-ant-oat followed by more visible characters, and nothing is stored; Dev flow does not ask Anthropic whether it works. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["setClaudeLogin"];
        post?: never;
        /**
         * Remove the caller's Claude login
         * @description Forgets the login and its sealed token. 204, whether or not there was one. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        delete: operations["removeClaudeLogin"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/display": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * What waits, for the front page
         * @description Read from what Dev flow stored and never from GitHub. waiting, the pull requests that wait for the caller and were not sent back, the runner's and Dev flow's own cards', with Dev flow's own cards refused as too thin, its cards waiting for the caller's answers to their questions and its features ready for review or waiting for the caller; stopped, those the runner stopped on, not sent back, and the caller's projects stopped because something landed on their default branch that Dev flow did not put there; projects, how many the caller has connected; stale, whether any project is stale or failing, as the waiting list says them; needs_relink, whether the caller's GitHub link has expired; and claude_login, where their Claude login stands, none, ok, due_soon or expired, as GET /devflow/api/claude-login says it. The core calls it with the user's token in the Authorization header. Without a valid token, 401 with an empty body.
         */
        get: operations["getDisplay"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/github/link": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Start linking the caller's GitHub account
         * @description Stores a single use state and a PKCE verifier for ten minutes, and answers the code host's address to send the browser to, carrying the state and the S256 challenge. The code host sends the browser back to /devflow/github/callback. Without a valid token, 401 with an empty body; from another origin, 403.
         */
        post: operations["startGitHubLink"];
        /**
         * Unlink the caller's GitHub account
         * @description Removes the account, its two tokens and any link started. Nothing is revoked at the code host. Without a valid token, 401 with an empty body; from another origin, 403.
         */
        delete: operations["unlinkGitHub"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/github/repositories": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The repositories the caller can connect
         * @description Every installation of the app the caller's GitHub account can reach, read as them, with the account it is on and its repositories, sorted by name, each saying whether it is already a project; and install_url, where to install the app. 409 when GitHub is not linked or the link has expired, 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["listConnectableRepositories"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/health": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Whether Dev flow serves
         * @description Needs no token. It answers whenever the process serves, and says nothing about what Dev flow depends on.
         */
        get: operations["health"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/paused": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Whether every project of the caller is paused
         * @description paused, whether the caller paused everything, and paused_at, when, null while not. Without a valid token, 401 with an empty body.
         */
        get: operations["getPaused"];
        /**
         * Pause every project of the caller, or continue them
         * @description paused true pauses every project of the caller, laid over each project's own pause: nothing new starts in any of them, as a project's pause holds it. false continues them, and a project paused on its own stays paused. Pausing what is paused, or continuing what runs, changes nothing, the time it was paused kept. 204; 422 when paused is missing. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["pauseEverything"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's projects
         * @description Each connected repository, by owner and name, with last_read_at, when its last read that succeeded was, null before any; paused_at, when the caller paused it, null while it is not paused on its own, a pause of everything being in held; and its summary, computed from what Dev flow stored: cards, its cards counted by group, needs_you (needs_answers, waiting_for_owner, stopped and refused), in_progress (reading, building, checking, reviewing and fixing), queued (queued and held) and done, an ignored card in none; workers, how many have said hello, last_seen_at, when the one seen last was seen, null with none, and held and cannot_run, the values listWorkers answers; setting_up, its setup steps done of total, six, while its state is setting_up, null once it is registered; and feature, its open feature, neither merged, closed nor ignored, with the most cards landed, the lowest number among equals, its number, title, landed, its cards in done, and total, its cards but an ignored one, null with none open. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["listProjects"];
        put?: never;
        /**
         * Connect a repository, which makes it a project
         * @description Asks GitHub, as the caller, whether the repository is among those of the installation they can reach, and only then stores it, with the owner and name GitHub gave. 201 with the project. 404 for a repository the caller cannot reach through that installation, 409 when it is already a project or GitHub is not linked, 422 naming a missing field, 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["connectRepository"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        /**
         * Disconnect a project
         * @description 204, and everything Dev flow stored for it goes with it. Nothing changes on GitHub. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        delete: operations["disconnectProject"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The project's features and cards
         * @description The project's features as its last read found them, in the order their cards would be taken, priority and then the oldest: each with its number, title and web_url, its state, the reason it is there, null when there is nothing to say, its priority, changed_at, and its cards in their sub-issue order; and every card on its own, newest change first. A card has its number, title and web_url, its state, the reason it is there, null when there is nothing to say, what it waits for, its priority and changed_at. Read from what Dev flow stored, never from GitHub. 404 for a project the caller does not have. Without a valid token, 401 with an empty body.
         */
        get: operations["listCards"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A card's whole story so far
         * @description The card as last read, its number, title, web_url, state and reason, and its pull_request, number and url, null before one was opened; each of its runs, newest first, with its kind, the worker it was handed to, when it started and finished, how long it took, its result and, for a failure, its reason and whether it lay in the card, the auth method, its tokens, input_tokens + cache_creation_input_tokens + output_tokens, and its cache reads beside them, each null when Claude gave none, the check's exit code and the last lines of its output, null when it did not run, the commit it left, has_log, whether it has lines of its log, read through readRunLog, and log_pruned, whether it had a log that was deleted once kept its 90 days, both false for a run from before logs were kept; the latest report in its form, the latest build's, null when no build has handed one back; and each review, newest first, with the commit the worker reviewed, its verdict, summary, findings, each blocking or not with its file, line, what and why, the reviewer's view on each decision for the owner, when it was recorded, whether its copy is posted, and whether it is current, for the card's head commit; and the screenshots of its head commit, the commit, the pictures' names paired desktop and phone by page, read through getCardScreenshot, and the worker's note, null when its newest build that succeeded tried none. And the feature it is a card of, its number and title, null for a card on its own; and run_now, what the page says of running it outside its project's schedules: outside_schedule, whether its project is outside them now, something_to_run, whether it has something left to run, false for a card of a feature, whose feature is run now instead, and on, whether runCardNow's override of it holds. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a card the caller does not have. Without a valid token, 401 with an empty body.
         */
        get: operations["getCard"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}/drop": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Drop a card the caller stopped, taking its pick-up label off as them
         * @description Takes the project's pick-up label off the card's issue as the caller's linked GitHub account, never as the app, and leaves the card ignored, "Dropped by you", its branch, any pull request and its issue otherwise as they were. A read of the project keeps it so while the label is off; putting the label back picks it up again, as any card. In order: 404 for a project or card the caller does not have; 409 when the card is not stopped by the caller; 409 when it is a card of a feature, whose pick-up label is its feature's; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once the label is off, or GitHub says the issue does not carry it, and the card is dropped. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer, with nothing changed in Dev flow. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["dropCard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}/merge": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Merge a card's pull request that waits, as the caller
         * @description Merges a card of Dev flow's own, from Waiting for you, with a merge commit, as the caller's linked GitHub account and never as the app, only while its pull request's head is head_commit, the commit its run built and checked and the page showed. In order: 404 for a project or card the caller does not have; 422 without head_commit; 409 when the card is not waiting for the caller with a pull request; 409 when the project is stopped, since something landed on its default branch that Dev flow did not put there; 409 when head_commit is not the commit the run built, after the project is read again; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once merged and the project read again, which makes the card done. 409 when GitHub says the head moved, after the project is read again, and when GitHub will not merge it or refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["mergeCard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}/run-again": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Run a card the caller stopped again
         * @description Puts a card the caller stopped back where the run they stopped started from: a build's in queued, a review's in reviewing, a fix's in fixing in the same round, with the reason that state has. Nothing counts against it: its failures in a row and its fix rounds are as they were. In order: 404 for a project or card the caller does not have; 409 when the card is not stopped by the caller, one Dev flow stopped included, which their comment runs again. 204 once it is back. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["runCardAgain"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}/run-now": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Run a card on its own now, outside its project's schedules, as the caller
         * @description Every job of the card, its build, review, fixes and readings, is handed out whether or not its project is inside its schedules, and nothing else of the project is. It skips the project's schedules and nothing else: a pause, the project's or everything's, still holds it. It lasts until it has nothing left to run, every card of a feature and the feature itself waiting for the caller, done, stopped, refused, needing answers, held or ignored, when Dev flow ends it, so a card added or sent back later keeps to the schedules. Setting it again changes nothing. 204. In order: 404 for a project or a card the caller does not have; 409 when it is a card of a feature, which runs with its feature, so its feature is run now instead, and when it has nothing left to run. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["runCardNow"];
        /**
         * Take back running a card now, as the caller
         * @description The card keeps to its project's schedules again; once this answers, no job is handed out by the override it took back, and a run under way finishes. Taking back one not set changes nothing. 204; 404 for a project or a card the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        delete: operations["runCardBySchedule"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}/screenshots/{name}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * One of the pictures the worker took of a card's head commit, read from GitHub as the app
         * @description The PNG the card's newest build that succeeded listed as name, read from the branch pr-screenshots of the project's own repository, at devflow/<number>/<the head commit's first seven characters>/<name>.png, as the app. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for a project or card the caller does not have, a card with no such build, or a name the build did not list, without asking GitHub; 404 when GitHub has no such file; 502 when what GitHub has is not a PNG or is over 5 MB; 503 when GitHub did not answer. Without a valid token, 401 with an empty body.
         */
        get: operations["getCardScreenshot"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/cards/{number}/send-back": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Send a card of Dev flow's own back, with a comment as the caller
         * @description Posts comment, trimmed and with nothing added, as the caller's linked GitHub account and never as the app: on the card's pull request for a card that waits for the caller, which the read of the project that follows takes, as any comment of his past the newest already taken, and starts a fix run with, which is not a review round; on the card's issue for a card that has stopped, which that read takes as a comment newer than the stop and runs the card again with; and on the card's issue for a card of a feature that has landed in the feature's branch, of a feature neither merged nor closed, which that read takes as a comment newer than the card's last run and sends the card back with, to be built again on a new branch. In order: 404 for a project or card the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the card neither waits for the caller with its pull request, nor has stopped, nor has landed in an open feature; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["sendBackCard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A feature's whole story so far
         * @description The feature as last read, its number, title, web_url, state and reason, null when there is nothing to say; its branch, the name and the commit it was at after the newest change its watch judged, or the one it was made at before any, null before the branch is made; its pull_request to the default branch, number and url, null before one is opened; and its cards in their sub-issue order, each with its number, title, web_url, state, reason, fix_rounds, the tokens of all its runs, input_tokens + cache_creation_input_tokens + output_tokens, and their cache reads beside them, the screenshots of its head commit as getCard has them, read through getCardScreenshot, and the decisions its latest report marked for the owner, each with the run_id of that report's run, its position among the report's decisions, what was chosen, the alternative, why, the reviewer_view from the newest review of the commit that run left, null without one, and the answer, fine or sent_back, null before one, can_send_back, whether the card has landed in the feature's branch of a feature neither merged nor closed, so it can be sent back, and sent_back, its last send back, how many times, the pull request it had landed in and the owner's comments, null when it was never sent back; its own runs, each with has_log and log_pruned as getCard has them, with tokens and cache_reads, their sums by the same definition, and the reviews of the whole feature, newest first; fix_rounds, the rounds its reviews have started; and rounds, each fix of the whole feature newest first, as getCard has a card's rounds; and run_now, what the page says of running it outside its project's schedules: outside_schedule, whether its project is outside them now, something_to_run, whether it has something left to run, and on, whether runFeatureNow's override of it holds. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a feature the caller does not have. Without a valid token, 401 with an empty body.
         */
        get: operations["getFeature"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}/decisions/{run}/{position}/fine": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Answers a decision of a feature's card "Fine as it is", in Dev flow alone
         * @description Keeps the answer fine to the decision at position among the decisions of the report of run, a build or fix of a card of the feature, when that decision is marked for the owner and that report is still the card's latest, the one getFeature shows. Kept once, as first given: answering again changes nothing and answers the answer kept, with its answered_at. Never written to GitHub. 404 for a project, feature, run or decision the caller does not have, or one not marked for the owner; 409 when the card has a newer report than that run's, so the page is to be read again. Without a valid token, 401 with an empty body.
         */
        post: operations["answerDecisionFine"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}/decisions/{run}/{position}/send-back": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Send a feature's card back from one of its decisions, with a comment as the caller
         * @description Posts a comment as the caller's linked GitHub account and never as the app, on the issue of the card whose report holds the decision at position of the report of run: the decision quoted, what was chosen and the alternative, then comment, trimmed. The read of the project that follows takes it as a comment newer than the card's last run and sends the card back with all of it, to be built again on a new branch, and the decision is kept answered sent_back. In order: 404 for a project, feature, run or decision the caller does not have, or one not marked for the owner; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the card has a newer report than that run's; 409 when the decision is already answered; 409 when its card has not landed in the feature's branch, or the feature is merged or closed; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted, the answer kept and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer, with no answer kept. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["sendBackDecision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}/merge": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * What merging a feature confirms, for the dialog that asks first
         * @description The feature's number and title, the default branch it merges into, its pull_request, number and url, the head_commit the merge names, review, approved when its newest review approved a commit that covers the head, changes when its newest review asked for changes, with its blocking findings as open_findings, and not_reviewed otherwise, and unanswered, how many decisions its cards marked for the caller have no answer, each of which merging confirms. Read from what Dev flow stored, never from GitHub; every text from GitHub or the reviewer is to be shown as text. In order: 404 for a project or feature the caller does not have; 409 as mergeFeature refuses, when it is not ready or waiting for the caller with its pull request, the project is stopped, or CI has not passed on its head. Without a valid token, 401 with an empty body.
         */
        get: operations["getFeatureMerge"];
        put?: never;
        /**
         * Merge a feature into the default branch, as the caller
         * @description Merges the feature's pull request into the default branch with a merge commit, as the caller's linked GitHub account and never as the app, only while its head is head_commit, the commit the page showed. In order: 404 for a project or feature the caller does not have; 422 without head_commit; 409 when the feature is not ready or waiting for the caller with its pull request; 409 when the project is stopped; 409 when CI has not passed on the feature's head, as the minute last read it; 409 when head_commit is not that head, after the project is read again; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once merged and the project read again, which makes the feature merged. 409 when GitHub says the head moved, after the project is read again, and when GitHub will not merge it or refuses, with its reason; 503 when GitHub did not answer. The review's verdict is not among the refusals: the confirmation says it. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["mergeFeature"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}/run-again": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Run a feature the caller stopped again
         * @description Clears the hold the caller's stop of one of the feature's own runs, a review, a fix or a conflict, put on it, so its next job is handed out, and it says what its state says. In order: 404 for a project or feature the caller does not have; 409 when the feature is not held by the caller's stop. 204 once it is released. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["runFeatureAgain"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}/run-now": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Run a feature now, outside its project's schedules, as the caller
         * @description Every job of the feature, its cards' builds, reviews, fixes and readings, its conflicts, its review and its fix, is handed out whether or not its project is inside its schedules, and nothing else of the project is. It skips the project's schedules and nothing else: a pause, the project's or everything's, still holds it. It lasts until it has nothing left to run, every card of a feature and the feature itself waiting for the caller, done, stopped, refused, needing answers, held or ignored, when Dev flow ends it, so a card added or sent back later keeps to the schedules. Setting it again changes nothing. 204. In order: 404 for a project or a feature the caller does not have; 409 when it has nothing left to run. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["runFeatureNow"];
        /**
         * Take back running a feature now, as the caller
         * @description The feature keeps to its project's schedules again; once this answers, no job is handed out by the override it took back, and a run under way finishes. Taking back one not set changes nothing. 204; 404 for a project or a feature the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        delete: operations["runFeatureBySchedule"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/features/{number}/send-back": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Send a whole feature of Dev flow's own back, with a comment as the caller
         * @description Posts comment, trimmed and with nothing added, as the caller's linked GitHub account and never as the app, on the feature's pull request to the default branch, which the read of the project that follows takes, as any comment of his past the newest already taken, and starts a fix of the whole feature with, which is not one of its review's rounds. In order: 404 for a project or feature the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the feature is neither ready nor waiting for the caller with its pull request; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["sendBackFeature"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/paused": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        /**
         * Pause a project, or continue it, as the caller
         * @description paused true pauses the project: no job of it is handed out, of any kind, a setup's and a check among them, until it is continued; a run under way finishes and its result is recorded as always, and reading GitHub goes on. false continues it. Pausing what is paused, or continuing what runs, changes nothing, the time it was paused kept. 204. In order: 422 when paused is missing; 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["pauseProject"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/refresh": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Read a project from GitHub now
         * @description 204 once it has been read, whether or not GitHub answered, which the list then says; 202 when the read is still under way after 10 seconds, and it goes on. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["refreshProject"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/resume": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Resume a stopped project, accepting what landed on its default branch
         * @description Accepts commit, the commit the page showed the project stopped by, and records who resumed it, when, and that commit, so the project hands out work again. In order: 404 for a project the caller does not have; 422 when commit is missing or is not a full commit id; 409 when it is not stopped; 409 when it is stopped by another commit than commit, which landed since the page was shown. 204 once resumed. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["resumeProject"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/runs/{run}/log": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A run's log, a page at a time
         * @description The run's lines after the seq given as after, 0 when none is given, in seq order, at most 1,000 of them, each with its seq, at, kind and text, and more, whether the run holds lines after the last of these, to read with after set to its seq. Every text is shown as text, never as HTML. A run's lines are kept 90 days after it finished, so a run whose log was pruned, as one from before logs were kept, answers none; its story says which, with has_log and log_pruned. 404 for a project or run the caller does not have. Without a valid token, 401 with an empty body.
         */
        get: operations["readRunLog"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/runs/{run}/log/events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A run's log, as it happens
         * @description Server-sent events, text/event-stream: a start event at once, data null, then a line event for each of the last 200 lines the run stored, after the seq in Last-Event-ID when one is sent, then one for each new line as the worker's log route stores it, its data the line, seq, at, kind and text, and its id the seq; each text is shown as text, never as HTML. Once the run has ended, its last lines and then an end event, data null, and the stream closes; a run that has ended sends them at once. It ends without one when the token it was opened with runs out, or when it falls too far behind, and EventSource opens it again from its Last-Event-ID. 404 for a project or run the caller does not have. Without a valid token, 401 with an empty body.
         */
        get: operations["streamRunLog"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/runs/{run}/stop": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Stop a run under way, as the caller
         * @description Marks the run to stop. Its worker's next post to the run's log, at most 15 seconds later, is answered stop: it ends Claude Code, or the check or the project's start while one runs, SIGTERM to the process and everything it started and SIGKILL 10 seconds later; a build, a fix, a conflict, a feature's fix and a setup run commit what the working copy holds and push their branch when it has a commit beyond its base; and the result comes back with the outcome stopped. The run is recorded stopped_by_owner, never a failure: a card's build, review or fix leaves the card stopped, "Stopped by you"; a feature's review, fix or conflict holds the feature, "Stopped by you"; a reading, a setup run or a check is put back to run again. 202 once marked, and again for a run already marked, which changes nothing. In order: 404 for a project or run the caller does not have; 409 when the run has already ended. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["stopRun"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/schedules": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * When a project runs
         * @description runs_by, always or schedule; time_zone, the caller's, which every window is wall clock in; and schedules, its windows in their order, each with id, name, days, the days it starts on, mon to sun in the week's order, from and to, HH:MM, a to earlier than from ending the next day, and active, whether it is on; next_start, when a window that is on next starts, after the one it runs in ends, looked for over the next 8 days, null with none and for a project that runs always; and running_until, when it runs by schedule and is inside a window that is on, when that ends, windows that meet or overlap taken as one, null otherwise. Both are instants with the zone's own offset. 404 for a project the caller does not have. Without a valid token, 401 with an empty body.
         */
        get: operations["getSchedules"];
        /**
         * Replace when a project runs, as the caller
         * @description Takes runs_by and schedules, the whole list, and replaces both in one transaction, answering what it left as getSchedules does. A window without an id gets a new one, and one with an id keeps it. The rules: runs_by is always or schedule; schedules is a list, empty for none, of at most 20 windows; a name is 1 to 60 characters once trimmed, unique in the project whatever its case; days is a non-empty set of mon, tue, wed, thu, fri, sat and sun, kept once each in the week's order; from and to are HH:MM in 24 hours, different; active is true or false; an id is given at most once. In order: 404 for a project the caller does not have; 422 naming each field that breaks a rule, runs_by, schedules or schedules[i].<field>, and nothing is saved. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["replaceSchedules"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/settings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A project's settings
         * @description The labels that pick and hold its cards, mark what needs the caller and mark a card for their review; whether a card's pull request waits for them, never, sometimes or always, with the three conditions of sometimes and the paths it watches; its trusted accounts, the GitHub logins whose cards, edits and comments count, the caller's linked login first and marked as theirs; the model that reads, builds and reviews its cards and the limits of its runs, run_budget_usd among them, the most a run may cost in whole US dollars; and questions, whether a card its reading found questions in waits for the caller's answers rather than going on the reader's recommendations; daily_token_budget, the tokens its runs may use in a day in the caller's time zone before it starts nothing new until midnight, null for off; and preset, the preset the settings match, fully_automatic, guided or only_i_merge, or custom, computed and never stored. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getProjectSettings"];
        /**
         * Change a project's settings
         * @description Takes all twenty, never a preset, and answers them as saved with the preset they match. A label is written, at most 50 characters, with no space at either end and no comma, and the four are different names whatever their case; waits_for_owner is never, sometimes or always, and sometimes has at least one of wait_when_marked, wait_when_paths and wait_when_decision; wait_paths is at most 100 glob patterns of at most 200 characters, none starting with ! or /, and a pattern given twice is kept once; trusted_logins is a list of GitHub logins that holds the caller's linked login, and a login given twice whatever its case is kept once; each model is opus, sonnet or haiku, and each limit within its range, run_budget_usd a whole number of US dollars from 1 to 100; questions is true or false; daily_token_budget is null for off or a whole number of millions of tokens from 1 000 000 to 10 000 000 000, and is never left out, which is refused with 400 rather than read as off. The first save also does the project's setup step settings, as accepting a setup run's proposal does. 422 naming each field that breaks a rule, 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["saveProjectSettings"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/setup": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A project's setup
         * @description state, setting_up until a check of any version of its .devflow/ has passed and its settings are accepted, then registered, and a project setting up starts no card's work, no reading or build; steps, in their order, each with whether it is done: setup, both files of .devflow/ on its default branch and valid, which a setup run writes when they are not, recipe, a version of .devflow/ read from its default branch, settings, the caller accepted the proposed settings or saved the settings once while the step waited, after the setup's pull request, without which it hands out no reading or build, values, each variable the project's newest setup run that succeeded listed and that can be given has a value, done when it listed none, worker, a worker built from that version said hello, and check, a check of that version passed; check, the newest check of that version, or of any before it is known, with its version, its commit, whether it passed, its exit code, the last 200 lines it printed, the names of its pictures of the project's address, why there are none, when, and whether another was asked for, or null before one ran; checking, whether a check of that version is running; problem, why .devflow/ is not valid as last read, or null; setup_command, the command that starts the project's setup worker from the base image, never holding the key; and setup, its newest setup run or null: its number, branch, state, queued, checking, reviewing, fixing, waiting_for_owner, stopped or done, reason, pull request, whether a run of it is under way, the commit it waits at, its newest report in its form, with what it proposes, the values its tests need and what it could not settle, and the review of that commit; and proposal, the preset, why and the paths its newest setup run that succeeded proposed, which the settings step offers to accept, or null; can_send_back, whether the newest check failed and may be sent back; and settings_waits, whether the settings step waits: not done, .devflow/ valid on the default branch and no sent back setup under way, the only time it is offered and a save does it. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getProjectSetup"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/setup/check-again": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Check a project's setup again
         * @description Asks for another check of the version whose newest check, the one getProjectSetup answers, failed: the next worker on that version that asks is handed it. 204. 409 when that check did not fail or another was already asked for; 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["checkProjectAgain"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/setup/checks/{check}/pictures/{name}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * One of a check's pictures of the project's address
         * @description The PNG its worker sent, as Dev flow stored it, checked when it came to be a PNG of at most 5 MB. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for a project the caller does not have, or a check or name it does not hold. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getSetupPicture"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/setup/merge": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Merge a project's setup as yourself
         * @description Merges the pull request of the project's setup run as the caller, with their linked GitHub token, naming head_commit, the commit the page showed: only while it waits for them, its review approved, and its project is not stopped. A setup's pull request is never merged by Dev flow, whatever the project's settings. 204, and the setup is followed and the project read again. 404 for a project the caller does not have; 422 without head_commit; 409 when no setup waits, the project is stopped, the commit moved since it was shown, GitHub is not linked, or GitHub would not merge it; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["mergeProjectSetup"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/setup/run-again": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Run a stopped setup again with a comment
         * @description Takes comment, trimmed, as the owner's comment on a setup run that stopped before it had a pull request, which has nothing on GitHub to comment on: the setup runs again with it, a fix of its branch, or a first run when nothing was pushed, with comment in its brief to be answered, as his comment on a setup's pull request runs one again. In order: 404 for a project the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the project's newest setup has not stopped, has a pull request, which his comment on GitHub runs again, or is running, or the project no longer needs a setup. 204 once taken. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["runProjectSetupAgain"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/setup/send-back": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Send a failed check back with a comment
         * @description Takes comment, trimmed, as the owner's comment on the failed check of the project's newest version of .devflow/: a new setup run starts from the default branch on the next free devflow/setup-<k>, its brief holding the two files as they were at the commit checked, the check's output and comment, and its pull request is the setup's, reviewed, fixed and waiting for the caller. In order: 404 for a project the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 4,000 characters; 409 when the newest check of the project's version did not fail, another check of it runs, or a setup is under way; 503 when GitHub did not answer for the files. 204 once taken. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["sendProjectSetupBack"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/stop-now": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Pause a project and stop every run of it under way, as the caller
         * @description Pauses the project, as pauseProject does, and then, in the same transaction, marks every run of it under way to stop, as stopRun marks one: each worker's next post to its run's log, at most 15 seconds later, is answered stop, it keeps what a pushing job did on its branch, and the run is recorded stopped_by_owner, its card "Stopped by you". Nothing of another project is stopped. stopped, how many runs were marked, a run marked before among them; 0 with none under way, and the project is paused all the same. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["stopProjectNow"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/values": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A project's values step
         * @description values, each name the project's newest setup run that succeeded listed, in its order, then every other name the caller gave, by name: its name; why, what needs it as the setup said, or null for one it did not list; asked, whether it listed it; set_at, when the caller last gave it, or null while it has none; and problem, why a listed name cannot be given, or null. Never a value. done, whether every listed name that can be given has a value, true when none is listed. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getProjectValues"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/values/{name}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        /**
         * Give one of a project's values
         * @description Seals value in the vault as the project's value named name, replacing any before it, for every job that runs the project's code, a build, a fix, a conflict, a feature's fix, a setup and a check, to carry into the environment of the agent, the check and the start; Dev flow scrubs it from everything it stores and posts. It is never answered again. In order: 404 for a project the caller does not have; 422 naming name when it is not capital letters, digits and underscores not beginning with a digit, is over 100 characters, begins with DEVFLOW_, CLAUDE_, ANTHROPIC_, GIT_ or PLAYWRIGHT_, or is PATH, HOME, LANG, USER, SHELL or TERM; 422 naming value when it is missing, holds a line break or a NUL, or is under 8 or over 4,096 characters, a shorter one being one scrubbing could not find without scrubbing ordinary words. 204 once sealed. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["setProjectValue"];
        post?: never;
        /**
         * Remove one of a project's values
         * @description Removes the project's value named name; jobs handed out after carry it no more. 204. 404 for a project the caller does not have, or a name it has no value for. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        delete: operations["removeProjectValue"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/worker-key": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Make the project's worker key
         * @description A new key for the project's workers, answered this once with save_command, the command that saves it in ~/.devflow/<project>.env for the start command to read: Dev flow keeps only its SHA-256 and never shows it again. It replaces the project's last key at once, so a worker holding that one is refused from then on. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["makeWorkerKey"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/projects/{id}/workers": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The project's workers
         * @description Every worker that has said hello with the project's key, by when it first did, with what it reported, when it was last seen, and rebuild: recipe when it runs another version of the recipe than Dev flow last read, base_image when it runs on another base image than the one released, or null; key_made_at, when the project's key was made, null before one has been; recipe_version, the recipe's version as last read, null before then or when the default branch has none; start_command, the command that builds and starts the project's worker; and held, why the project hands out no work whichever worker asks, the first that applies: all_paused, when the caller paused everything, paused, when the caller paused the project, stopped, when something landed on its default branch that Dev flow did not put there, unwatched, when its default branch has not been read for five minutes, no_claude_login when its user has given no Claude login, claude_login_expired when it is past its renewal, or null; cannot_run, once six runs in a row have failed outside their cards, "Dev flow cannot run <project>'s cards: <the last reason>", until a run succeeds, or null; and budget, once the project's runs that finished today in the caller's time zone used its daily budget, with used, budget, lifts_at, the next midnight there in its own offset, and sentence, "Daily budget reached, runs again at <day time>", the one rule's own, which holds it after a pause and before every other hold in held, or null. The key itself is never in it. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["listWorkers"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/running": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's runs under way
         * @description Every run under way of the caller's projects, oldest first, as Running now shows each: its project, its worker by the name it said hello with, its kind, state, running or checking once its check runs, stopping when the caller marked it to stop and its worker has not ended it yet, its card's or its feature's number and title, null for a setup's run or a check, a fix's round, the model its job was handed out with, null for a check, when it started, its tokens so far, input_tokens + cache_creation_input_tokens + output_tokens, and its cache reads so far beside them; busy_workers, how many workers hold them; and next, the cards that come next, Next up: every card of the caller's projects queued, waiting to be read, or waiting for its review or its fix round, with no run under way, in each project's queue order and across projects by priority label and then age, at most 50, each with its project, number, title, state, the job due for it, the sentence of the reason the one rule gives it, null for one that runs as soon as a worker asks, and where that reason is acted on, link, card with link_card, waiting_for_you, project_page, settings, schedules, the card "When it runs" on its project's settings page, or budget, the card "Usage" there, where its daily budget is raised, null where it has no place, feature, the number of the feature it is a card of, null for a card on its own, and run_now, whether the one rule holds it only for its project's schedules, so runFeatureNow on its feature, or runCardNow on a card on its own, would run it; and next_more, how many come after those 50. Without a valid token, 401 with an empty body.
         */
        get: operations["listRunning"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/running/events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's runs under way, as they change
         * @description Server-sent events, text/event-stream: a running event at once, its data what GET /devflow/api/running answers, and another whenever that changed, a run started or ended, its state or its tokens moved, or a card next came, went or changed its reason, read again every 2 seconds; nothing while nothing changes. It ends when the token it was opened with runs out, and EventSource opens it again. Without a valid token, 401 with an empty body.
         */
        get: operations["streamRunning"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/status": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Which code host Dev flow talks to, and the caller's GitHub link
         * @description code_host is fake when GitHub is faked, which every Dev flow page then says in a banner, and github otherwise. github.state is none, linked or needs_relink, with the login it was linked as, null with none. Without a valid token, 401 with an empty body.
         */
        get: operations["status"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/time-zone": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's time zone
         * @description time_zone, the IANA id every schedule of the caller's and every day Dev flow counts for them is read in; Europe/Stockholm until they set another. Without a valid token, 401 with an empty body.
         */
        get: operations["getTimeZone"];
        /**
         * Set the caller's time zone
         * @description Takes time_zone, an IANA id: UTC, or an area and a place joined by /, of letters, digits, _, + and -, at most 64 characters, that .NET's own zones know, such as Europe/Stockholm; a Windows id such as W. Europe Standard Time is refused. Used by every project of the caller's, its schedules and its days, at once. 200 with the zone as saved; 422 naming time_zone for anything else. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["setTimeZone"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/usage": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * What the caller's projects, features and cards used
         * @description Counts the runs started in period, 7d or 30d, rolling, so many times 24 hours back from now, or all, 30d without one. period, as counted, and projects, each of the caller's projects with a run in it, most tokens first: its id and name; runs, agent_ms, the sum of the runs' times, each as its worker measured it, or from its start to its end, or to now while it runs; tokens, input_tokens + cache_creation_input_tokens + output_tokens; and cache_reads beside them, never in them; setting_up, the same for its checks and its setup's runs, null with none; features, each with a run in the period of its own or of one of its cards, most tokens first, with its number, title, the same figures over its own runs and its cards', elapsed_ms, from the first start of any of them to the last end, or now while one runs, fix_rounds, the rounds of the whole feature its reviews started, sent_back, how many times its cards were sent back, own, the same figures for its own runs alone, its readings, reviews, fixes and conflicts, null with none, and cards, its cards with a run in the period; cards, its cards on their own with a run in the period; and days, its tokens per day, the last 30 days ending today in Dev flow's time zone, Europe/Stockholm, oldest first, whatever the period, each its date and the tokens of the project's runs started on it, 0 with none. A card, most tokens first, has its number, title, null for one Dev flow no longer holds, the same figures, elapsed_ms over all its runs, fix_rounds, the rounds its reviews started, and sent_back, how many times it was sent back; elapsed, fix rounds and sends back are the card's or the feature's whole, not the period's. Read from what Dev flow stored, never from GitHub. 422 naming period for any other. Without a valid token, 401 with an empty body.
         */
        get: operations["getUsage"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/waiting": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * What waits for the caller, as last read
         * @description Read from what Dev flow stored and never from GitHub. items are the pull requests that are the caller's turn in every project, by project and number; own_items are Dev flow's own, read from its own tables, each named by its kind: project_stopped, a project stopped because something landed on its default branch that Dev flow did not put there, with the commit, who did it, when, and the commit's address on GitHub; and budget_reached, a project whose runs that finished today in the caller's time zone used its daily budget, with used, the tokens by the one definition, budget, lifts_at, the next midnight there in its own offset, and time_zone, worked out as the list is read and gone once the budget is raised or the day ends; the stopped projects first, then the budgets, each by project; cards are Dev flow's own cards whose pull requests wait for the caller, each with its pull request, the head commit its run built and checked, the branch it merges into, the report's summary, the decisions the agent marked for the caller with the count of the others, the check Dev flow ran, review, the reviewer's verdict on its head commit with its summary and how many findings block, null when it has none, and can_merge, false while the project is stopped, by project and number; stopped_cards are Dev flow's own cards that stopped, each with its issue's address, where a comment runs it again, why it stopped, when, its pull request's address or null, stopped_by_owner, true for a card the caller stopped, which Run again and Drop act on in place of a comment, and feature_number, its feature's number or null for a card on its own, by project and number; stopped_features are Dev flow's own features held because the caller stopped one of their own runs, a review, a fix or a conflict, each with its parent's address, the state it is held in, its reason and when it was stopped, which Run again releases, by project and number; refused_cards are Dev flow's own cards refused as too thin to build, each with its issue's address, where an edit or a comment reads it again, what is missing, each a line, and when it was refused, by project and number; question_cards are Dev flow's own cards whose reading found questions, with the project's questions on, that wait for the caller's answers, each with its id, which the answers route names, its issue's address, its feature, number and title, or null for a card on its own, reading_id, the reading that asked them, each question with the reader's recommendation and why, in order, and when it was read, by project and number; failing_projects are the projects whose runs have failed outside their cards six times in a row, each with message, "Dev flow cannot run <project>'s cards: <the last reason>", the reason, how many failures, since when and until when it hands out no job, shown until a run succeeds, by project; decisions are the open issues Dev flow opened for the decisions the agent marked for the caller on cards it merged, each with what was chosen, the alternative, why and the reviewer's view, by project, oldest first, until the caller closes it on GitHub; setup_projects are the projects whose newest check of their setup failed with no other asked for since, so that version's workers get nothing, each with its state, setting_up or registered, the version, the check's exit code and when it failed, by project, its output and Check again being on the project's page; count is the items not sent back, the own items, the cards, the stopped and refused cards, the stopped features, the failing projects, the decisions, the features, the question cards and the setup projects, plus one when needs_relink; projects says for each when it was last read, stale when that is more than five minutes ago or never, and failing when its last read failed. Without a valid token, 401 with an empty body.
         */
        get: operations["listWaiting"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/waiting/{id}/merge": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Merge a waiting pull request, as the caller
         * @description Merges with a merge commit, as the caller's linked GitHub account, only while the pull request's head is head_commit, the commit the page showed. In order: 404 for an item the caller does not have; 422 without head_commit; 409 when it is stopped, sent back, or its checks are not green; 409 when head_commit is not the stored head; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once merged and the project read again. 409 when GitHub says the head moved, after the project is read again, and when GitHub will not merge it or refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["mergeWaiting"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/waiting/{id}/screenshots/{name}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * One of a waiting item's screenshots, read from GitHub as the app
         * @description The PNG the item's pull request body showed as name, read from the branch pr-screenshots of the item's own repository, at <number>/<name>.png, as the app. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for an item the caller does not have, or a name its body did not show, without asking GitHub; 404 when GitHub has no such file; 502 when what GitHub has is not a PNG or is over 5 MB; 503 when GitHub did not answer. Without a valid token, 401 with an empty body.
         */
        get: operations["getWaitingScreenshot"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/waiting/{id}/send-back": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Send a waiting or stopped pull request back, with a comment as the caller
         * @description Posts comment, trimmed and with nothing added, on the pull request as the caller's linked GitHub account, which the runner reads as the owner's input on its next turn. In order: 404 for an item the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted, with the item marked sent back at once and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        post: operations["sendBackWaiting"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/whoami": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The account the request's token names
         * @description From the token alone. Without a valid token, 401 with an empty body.
         */
        get: operations["whoami"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/hello": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * A worker introduces itself
         * @description The worker's id, a UUID it made when it started, its name, the container's hostname, and the recipe version and base image it runs. Records the worker in the key's project, or brings it up to date, and marks it seen. 204. 422 naming a field that is missing or breaks a rule: every one is required, and each text is at most 255 characters. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerHello"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/jobs/next": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * A worker asks for work
         * @description Marks the worker seen and answers its next job, 200 with Cache-Control: no-store: the first of its project's queued cards that waits for nothing, by priority, high, none, low, then oldest first, moved to building, with a run recorded for this worker. The job holds the card, the repository, the branch, the brief, the report's schema, the check, the run's user's Claude token, a push key narrowed to the repository and its expiry, the committer and the limits; and values, the project's values by name, for the environment of the agent, the check and the start, on every job that runs the project's code, a build, a fix, a conflict, a feature's fix, a setup and a check, and empty on a reading and a review. 204 when there is no work for it: a project that is stopped, or whose default branch has not been watched for five minutes, no card to take, a user without a Claude login or with one past its renewal, a worker that needs a rebuild, running another version of the recipe than Dev flow last read or another base image than the one released, or the code host not answering what a job needs, such as the project's .devflow/project.json. 409 for a worker that has not said hello in the key's project, which says hello and asks again; 422 without worker_id. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerNextJob"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/jobs/{id}/log": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * A running job's log, and its heartbeat
         * @description What the worker sends from the job's start until its result is sent: every 2 seconds when it has new lines, every 15 with none. lines, at most 500, each with seq, its place in the run's log from 1, a positive whole number; at, a time; kind, one of text, tool, tool_result, check, system; and text, 1 to 4000 characters, a longer one cut to 4000 ending in "… (cut)". Each text has every value of the project scrubbed from it before it is stored. A line whose seq the run already holds is skipped, so a retried post never doubles one, and gaps are allowed. A run keeps at most 20,000 lines; past that one system line, "The rest of this run's log was not kept", and nothing more. usage, optional, the run's token figures so far, input_tokens, cache_creation_input_tokens, cache_read_input_tokens and output_tokens, each a whole number, 0 or more; it sets the run's figures, which its result's usage replaces. Every post, with lines or none, is the run's heartbeat and marks the worker seen: a run whose worker has sent nothing for 2 minutes is lost. 200 with stop, whether the owner marked the run to stop, so the worker ends it now. Only for the worker holding the job, named by worker_id, while it runs; 404 for any other worker, a job that is not one of the project's, or one that has finished. 422 naming a field that is missing or breaks a rule. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerJobLog"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/jobs/{id}/progress": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * A running job moves on
         * @description With state checking, the agent has finished and the check runs: the card moves from building to checking. 204. Only for the worker holding the job, named by worker_id, while it runs; 404 for any other worker, a job that is not one of the project's, or one that has finished. 422 without worker_id, or with a state that is not checking. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerJobProgress"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/jobs/{id}/push-key": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * A fresh push key for a running job
         * @description A new push key for the job's repository, narrowed to fetching and pushing code, with when it expires, 200 with Cache-Control: no-store, since a key lasts an hour and a run up to 90 minutes. Only for the worker holding the job, named by worker_id, while the job runs; 404 for any other worker, a job that is not one of the project's, or one that has finished. 503 when the code host made no key now, which the worker asks for again; 422 without worker_id. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerPushKey"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/jobs/{id}/result": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * A job has ended
         * @description What the worker sends when its job ends, whatever happened: outcome, succeeded with the agent's report, failed with the failure's reason and whether it lies in the card, or stopped, when the owner stopped the run, with report, failure, check and pictures null and the commit a pushing job pushed of what it did, or none; the check's exit code and the last 200 lines of its output, null when it did not run; the branch, which must be the job's, and its head commit; the auth method; Claude's usage as it came, an object; the duration in milliseconds; the last 200 lines of stderr; and permission_denials, the actions the agent was refused, an array or null. Each is stored on the run as it came. A build's report is read against contracts/devflow/report.schema.json, and one that does not match is a failure in the card; a review's report is its verdict, read against contracts/devflow/verdict.schema.json, and one that does not match is a failure in the run, never a verdict. A review's head_commit is the commit it was handed, or null on a failure; any other is 422. A run stopped is stopped_by_owner, never a failure: a card's build, review or fix leaves the card stopped, "Stopped by you", counting nothing; a feature's own run holds the feature; a reading, a setup or a check is put back. A failure outside the card puts the card back in queued, counting nothing against it; a failure in the card, a report that does not match, a report that stopped, a check that did not run or failed stop it with why; otherwise the card waits, checked, for its pull request. A check runs no agent: its report is null, a success means the check ran and carries its result, which is the verdict on the version, and pictures, its address taken at both sizes as PNGs of at most 5 MB, base64, named as screenshots names them; pictures on any other job are 422. A setup's success is validated by Dev flow at the commit it names, its .devflow/Dockerfile by the recipe route's rules and its .devflow/project.json by its schema, as its check; 503 when GitHub did not answer, and the worker sends the result again. 204. Only for the worker holding the job while it runs, and once; 404 otherwise. 422 naming a field that is missing or breaks a rule. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerJobResult"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/devflow/api/worker/recipe": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The project's recipe, to build a worker from
         * @description The file .devflow/Dockerfile of the key's project, from its repository's default branch, read as the app at the newest commit that changed anything under .devflow/, the project's setup version: text/plain, with that commit's id in X-Devflow-Recipe-Version, X-Content-Type-Options: nosniff and Cache-Control: no-store. 404 when the repository has none; 422 naming the line when it copies files into the image, a COPY or an ADD in any case, after continued lines are joined, or when it is not UTF-8 text of at most 64 KB; 503 when GitHub did not answer. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body.
         */
        get: operations["workerRecipe"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AllPausedState: {
            paused: boolean;
            /** Format: date-time */
            paused_at: null | string;
        };
        AnswersRequest: {
            answers: null | components["schemas"]["GivenAnswer"][];
            /** Format: uuid */
            reading_id: null | string;
        };
        ApiKeyRequest: {
            key: null | string;
        };
        ApiKeyStatus: {
            /** Format: date-time */
            given_at: null | string;
            set: boolean;
        };
        Attention: {
            attention: boolean;
        };
        BudgetHold: {
            /** Format: int64 */
            budget: number;
            /** Format: date-time */
            lifts_at: string;
            sentence: string;
            /** Format: int64 */
            used: number;
        };
        CardCheck: {
            command: null | string;
            commit: string;
            /** Format: int32 */
            exit_code: number;
        };
        CardCounts: {
            /** Format: int32 */
            done: number;
            /** Format: int32 */
            in_progress: number;
            /** Format: int32 */
            needs_you: number;
            /** Format: int32 */
            queued: number;
        };
        CardFeature: {
            /** Format: int32 */
            number: number;
            title: string;
        };
        CardItem: {
            base: string;
            can_merge: boolean;
            /** Format: date-time */
            changed_at: string;
            check: components["schemas"]["CardCheck"];
            decisions: components["schemas"]["OwnerDecision"][];
            head_commit: string;
            /** Format: int32 */
            number: number;
            /** Format: int32 */
            other_decisions: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: int32 */
            pull_request_number: number;
            /** Format: uri */
            pull_request_url: string;
            reason: null | string;
            review: null | components["schemas"]["CardItemReview"];
            screenshots: null | components["schemas"]["CardScreenshots"];
            summary: string;
            title: string;
            /** Format: uri */
            web_url: string;
        };
        CardItemReview: {
            /** Format: int32 */
            blocking: number;
            commit: string;
            summary: string;
            verdict: string;
        };
        CardList: {
            cards: components["schemas"]["CardView"][];
            features: components["schemas"]["FeatureView"][];
        };
        CardMerge: {
            approved: string;
            base: string;
            commit: string;
            /** Format: date-time */
            merged_at: string;
        };
        CardPullRequest: {
            /** Format: int32 */
            number: number;
            url: string;
        };
        CardQuestion: {
            answer: string;
            question: string;
            recommendation: string;
            recommended: boolean;
            why: string;
        };
        CardReadingView: {
            answered: boolean;
            commented: boolean;
            missing: string[];
            outcome: string;
            questions: components["schemas"]["CardQuestion"][];
            /** Format: date-time */
            read_at: string;
        };
        CardReview: {
            commit: string;
            current: boolean;
            findings: components["schemas"]["ReviewFinding"][];
            for_owner: components["schemas"]["ReviewOwnerView"][];
            posted: boolean;
            /** Format: date-time */
            reviewed_at: string;
            /** Format: uuid */
            run_id: string;
            summary: string;
            verdict: string;
        };
        CardRound: {
            comments: string[];
            fixed_commit: null | string;
            replies: components["schemas"]["ReportReply"][];
            result: null | string;
            /** Format: uuid */
            review_after_run_id: null | string;
            /** Format: uuid */
            review_run_id: null | string;
            /** Format: int32 */
            round: null | number;
            /** Format: uuid */
            run_id: string;
        };
        CardRun: {
            auth_method: null | string;
            /** Format: int64 */
            cache_reads: null | number;
            check: null | components["schemas"]["RunCheck"];
            /** Format: int64 */
            duration_ms: null | number;
            /** Format: date-time */
            finished_at: null | string;
            has_log: boolean;
            head_commit: null | string;
            /** Format: uuid */
            id: string;
            in_card: null | boolean;
            kind: string;
            log_pruned: boolean;
            reason: null | string;
            result: null | string;
            /** Format: date-time */
            started_at: string;
            stderr_tail: null | string;
            /** Format: int64 */
            tokens: null | number;
            worker: string;
        };
        CardScreenshots: {
            commit: string;
            groups: components["schemas"]["ScreenshotGroup"][];
            note: null | string;
        };
        CardSentBack: {
            comments: string[];
            pull_request: components["schemas"]["CardPullRequest"];
            /** Format: int32 */
            times: number;
        };
        CardStoryView: {
            earlier_readings: components["schemas"]["CardReadingView"][];
            feature: null | components["schemas"]["CardFeature"];
            merge: null | components["schemas"]["CardMerge"];
            /** Format: int32 */
            most_rounds: number;
            /** Format: int32 */
            number: number;
            pull_request: null | components["schemas"]["CardPullRequest"];
            reading: null | components["schemas"]["CardReadingView"];
            reason: null | string;
            report: null | components["schemas"]["Report"];
            repository: string;
            reviews: components["schemas"]["CardReview"][];
            rounds: components["schemas"]["CardRound"][];
            run_now?: null | components["schemas"]["RunNowState"];
            runs: components["schemas"]["CardRun"][];
            screenshots: null | components["schemas"]["CardScreenshots"];
            state: string;
            title: string;
            web_url: string;
        };
        CardView: {
            /** Format: date-time */
            changed_at: string;
            /** Format: int32 */
            number: number;
            priority: string;
            reason: null | string;
            state: string;
            title: string;
            waits_for: string[];
            web_url: string;
        };
        CheckResult: {
            /** Format: int32 */
            exit_code: null | number;
            output: null | string;
        };
        ClaudeLoginRequest: {
            token: null | string;
        };
        /** @enum {unknown} */
        ClaudeLoginState: "none" | "ok" | "due_soon" | "expired";
        ClaudeLoginStatus: {
            /** Format: date-time */
            given_at: null | string;
            /** Format: date-time */
            renew_by: null | string;
            set: boolean;
            state: components["schemas"]["ClaudeLoginState"];
        };
        /** @enum {unknown} */
        CodeHostMode: "fake" | "github";
        ConnectRequest: {
            /** Format: int64 */
            installation_id: null | number;
            /** Format: int64 */
            repository_id: null | number;
        };
        Connectable: {
            /** Format: uri */
            install_url: string;
            installations: components["schemas"]["ConnectableInstallation"][];
        };
        ConnectableInstallation: {
            account: string;
            /** Format: int64 */
            id: number;
            repositories: components["schemas"]["ConnectableRepository"][];
        };
        ConnectableRepository: {
            connected: boolean;
            /** Format: int64 */
            id: number;
            name: string;
            owner: string;
        };
        DecisionAnswer: {
            answer: string;
            /** Format: date-time */
            answered_at: string;
        };
        DecisionItem: {
            alternative: null | string;
            /** Format: int32 */
            card_number: number;
            card_title: string;
            /** Format: uri */
            card_url: string;
            chosen: string;
            /** Format: uuid */
            id: string;
            /** Format: int32 */
            issue_number: number;
            /** Format: uri */
            issue_url: string;
            /** Format: date-time */
            opened_at: string;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: int32 */
            pull_request_number: number;
            /** Format: uri */
            pull_request_url: string;
            reviewer_view: null | string;
            why: null | string;
        };
        DevFlowStatus: {
            code_host: components["schemas"]["CodeHostMode"];
            github: components["schemas"]["GitHubLinkStatus"];
        };
        DisplayData: {
            claude_login: components["schemas"]["ClaudeLoginState"];
            needs_relink: boolean;
            /** Format: int32 */
            projects: number;
            stale: boolean;
            /** Format: int32 */
            stopped: number;
            /** Format: int32 */
            waiting: number;
        };
        FailingProject: {
            /** Format: int32 */
            failures: number;
            message: string;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            reason: string;
            /** Format: date-time */
            since: string;
            /** Format: date-time */
            wait_until: string;
        };
        FeatureBranch: {
            commit: null | string;
            name: string;
        };
        FeatureCard: {
            /** Format: int64 */
            cache_reads: number;
            can_send_back: boolean;
            decisions: components["schemas"]["FeatureDecision"][];
            /** Format: int32 */
            fix_rounds: number;
            /** Format: int32 */
            number: number;
            reason: null | string;
            screenshots: null | components["schemas"]["CardScreenshots"];
            sent_back: null | components["schemas"]["CardSentBack"];
            state: string;
            title: string;
            /** Format: int64 */
            tokens: number;
            web_url: string;
        };
        FeatureDecision: {
            alternative: string;
            answer: null | components["schemas"]["DecisionAnswer"];
            chosen: string;
            /** Format: int32 */
            position: number;
            reviewer_view: null | string;
            /** Format: uuid */
            run_id: string;
            why: string;
        };
        FeatureItem: {
            /** Format: date-time */
            changed_at: string;
            merge: null | components["schemas"]["FeatureMergeOffer"];
            /** Format: int32 */
            number: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: int32 */
            pull_request_number: null | number;
            /** Format: uri */
            pull_request_url: null | string;
            reason: null | string;
            review: null | components["schemas"]["FeatureItemReview"];
            state: string;
            title: string;
            /** Format: int32 */
            unanswered: number;
            /** Format: uri */
            web_url: string;
        };
        FeatureItemReview: {
            /** Format: int32 */
            blocking: number;
            commit: string;
            summary: string;
            verdict: string;
        };
        FeatureMergeConfirmation: {
            head_commit: string;
            into: string;
            /** Format: int32 */
            number: number;
            open_findings: components["schemas"]["ReviewFinding"][];
            pull_request: components["schemas"]["CardPullRequest"];
            review: string;
            title: string;
            /** Format: int32 */
            unanswered: number;
        };
        FeatureMergeOffer: {
            head_commit: string;
            into: string;
        };
        FeatureProgress: {
            /** Format: int32 */
            landed: number;
            /** Format: int32 */
            number: number;
            title: string;
            /** Format: int32 */
            total: number;
        };
        FeatureStoryView: {
            branch: null | components["schemas"]["FeatureBranch"];
            /** Format: int64 */
            cache_reads: number;
            cards: components["schemas"]["FeatureCard"][];
            default_branch: null | string;
            /** Format: int32 */
            fix_rounds: number;
            merge: null | components["schemas"]["FeatureMergeOffer"];
            /** Format: int32 */
            most_rounds: number;
            /** Format: int32 */
            number: number;
            pull_request: null | components["schemas"]["CardPullRequest"];
            reason: null | string;
            repository: string;
            reviews: components["schemas"]["CardReview"][];
            rounds: components["schemas"]["CardRound"][];
            run_now?: null | components["schemas"]["RunNowState"];
            runs: components["schemas"]["CardRun"][];
            state: string;
            stopped_by_owner: boolean;
            title: string;
            /** Format: int64 */
            tokens: number;
            web_url: string;
        };
        FeatureView: {
            cards: components["schemas"]["CardView"][];
            /** Format: date-time */
            changed_at: string;
            /** Format: int32 */
            number: number;
            priority: string;
            reason: null | string;
            state: string;
            title: string;
            web_url: string;
        };
        /** @enum {unknown} */
        GitHubLinkState: "none" | "linked" | "needs_relink";
        GitHubLinkStatus: {
            login: null | string;
            state: components["schemas"]["GitHubLinkState"];
        };
        GivenAnswer: {
            accept: null | boolean;
            text: null | string;
        };
        Health: {
            status: string;
        };
        HelloRequest: {
            base_image: null | string;
            name: null | string;
            recipe_version: null | string;
            /** Format: uuid */
            worker_id: null | string;
        };
        HttpValidationProblemDetails: {
            detail?: null | string;
            errors?: {
                [key: string]: string[];
            };
            instance?: null | string;
            /** Format: int32 */
            status?: null | number;
            title?: null | string;
            type?: null | string;
        };
        Job: {
            base: string;
            branch: string;
            brief: string;
            card: null | components["schemas"]["JobCard"];
            check: string;
            claude_token: string;
            committer: components["schemas"]["JobCommitter"];
            /** Format: uuid */
            job_id: string;
            kind: string;
            limits: components["schemas"]["JobLimits"];
            model: string;
            push_key: string;
            /** Format: date-time */
            push_key_expires_at: string;
            report_schema: components["schemas"]["JsonElement"];
            repository: components["schemas"]["JobRepository"];
            /** @default  */
            start: string;
            to_check?: null | components["schemas"]["JobCheck"];
            to_fix?: null | components["schemas"]["JobFix"];
            to_read?: null | components["schemas"]["JobRead"];
            to_resolve?: null | components["schemas"]["JobConflict"];
            to_review?: null | components["schemas"]["JobReview"];
            to_review_feature?: null | components["schemas"]["JobFeatureReview"];
            /** @default  */
            url: string;
            values?: null | components["schemas"]["JobValue"][];
        };
        JobCard: {
            body: string;
            /** Format: int32 */
            number: number;
            title: string;
        };
        JobCheck: {
            commit: string;
            version: string;
        };
        JobCommitter: {
            email: string;
            name: string;
        };
        JobConflict: {
            default_commit: string;
            feature_commit: string;
        };
        JobFeatureCard: {
            handover: string;
            /** Format: int32 */
            number: number;
            summary: null | string;
            title: string;
            verdict: null | string;
        };
        JobFeatureCheck: {
            command: string;
            commit: string;
            /** Format: int32 */
            exit_code: number;
            output: string;
        };
        JobFeatureReview: {
            cards: components["schemas"]["JobFeatureCard"][];
            check: null | components["schemas"]["JobFeatureCheck"];
            commit: string;
            earlier?: null | components["schemas"]["JobRound"][];
            remerge_diff: null | string;
        };
        JobFix: {
            commit: string;
            /** Format: int32 */
            round: null | number;
        };
        JobLimits: {
            /** Format: double */
            budget_usd: number;
            /** Format: int32 */
            minutes: number;
            /** Format: int32 */
            turns: number;
        };
        JobRead: {
            cards: components["schemas"]["JobCard"][];
        };
        JobRepository: {
            /** Format: uri */
            clone_address: string;
            default_branch: string;
            name: string;
            owner: string;
        };
        JobReview: {
            check: components["schemas"]["CheckResult"];
            commit: string;
            earlier: components["schemas"]["JobRound"][];
            report: components["schemas"]["JsonElement"];
            screenshots?: null | components["schemas"]["ResultScreenshots"];
        };
        JobRound: {
            comments: string[];
            findings: components["schemas"]["JsonElement"];
            fixed_commit: string;
            replies: components["schemas"]["JsonElement"];
            reviewed_commit: null | string;
            /** Format: int32 */
            round: null | number;
            summary: null | string;
            verdict: null | string;
        };
        JobValue: {
            name: string;
            value: string;
        };
        JsonElement: unknown;
        LinkStarted: {
            /** Format: uri */
            authorize_url: string;
        };
        ListedProject: {
            /** Format: date-time */
            created_at: string;
            /** Format: uuid */
            id: string;
            /** Format: int64 */
            installation_id: number;
            /** Format: date-time */
            last_read_at: null | string;
            name: string;
            owner: string;
            /** Format: date-time */
            paused_at: null | string;
            /** Format: int64 */
            repository_id: number;
            summary: components["schemas"]["ProjectSummary"];
        };
        LogAnswer: {
            stop: boolean;
        };
        LogLine: {
            /** Format: date-time */
            at: null | string;
            kind: null | string;
            /** Format: int64 */
            seq: null | number;
            text: null | string;
        };
        LogRequest: {
            lines: null | components["schemas"]["LogLine"][];
            usage?: null | components["schemas"]["LogUsage"];
            /** Format: uuid */
            worker_id: null | string;
        };
        LogUsage: {
            /** Format: int64 */
            cache_creation_input_tokens: null | number;
            /** Format: int64 */
            cache_read_input_tokens: null | number;
            /** Format: int64 */
            input_tokens: null | number;
            /** Format: int64 */
            output_tokens: null | number;
        };
        MergeRequest: {
            head_commit: null | string;
        };
        NextCard: {
            /** Format: int32 */
            feature: null | number;
            job: null | string;
            link: null | string;
            /** Format: int32 */
            link_card: null | number;
            /** Format: int32 */
            number: number;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            reason: null | string;
            run_now: boolean;
            state: string;
            title: string;
        };
        NextJobRequest: {
            /** Format: uuid */
            worker_id: null | string;
        };
        OwnItem: components["schemas"]["OwnItemProjectStoppedItem"] | components["schemas"]["OwnItemBudgetReachedItem"];
        OwnItemBudgetReachedItem: {
            /** Format: int64 */
            budget: number;
            /** @enum {string} */
            kind?: "budget_reached";
            /** Format: date-time */
            lifts_at: string;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            time_zone: string;
            /** Format: int64 */
            used: number;
        };
        OwnItemProjectStoppedItem: {
            activity_type: string;
            actor: null | string;
            branch: string;
            commit: string;
            /** Format: uri */
            commit_url: string;
            /** @enum {string} */
            kind?: "project_stopped";
            /** Format: date-time */
            landed_at: string;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: date-time */
            stopped_at: string;
        };
        OwnerDecision: {
            alternative: null | string;
            chosen: string;
            why: null | string;
        };
        PauseRequest: {
            paused: null | boolean;
        };
        ProblemDetails: {
            detail?: null | string;
            instance?: null | string;
            /** Format: int32 */
            status?: null | number;
            title?: null | string;
            type?: null | string;
        };
        ProgressRequest: {
            state: null | string;
            /** Format: uuid */
            worker_id: null | string;
        };
        Project: {
            /** Format: date-time */
            created_at: string;
            /** Format: uuid */
            id: string;
            /** Format: int64 */
            installation_id: number;
            name: string;
            owner: string;
            /** Format: int64 */
            repository_id: number;
        };
        ProjectFreshness: {
            failing: boolean;
            /** Format: uuid */
            id: string;
            /** Format: date-time */
            last_success_at: null | string;
            name: string;
            owner: string;
            stale: boolean;
        };
        ProjectList: {
            projects: components["schemas"]["ListedProject"][];
        };
        ProjectSchedules: {
            /** Format: date-time */
            next_start: null | string;
            /** Format: date-time */
            running_until: null | string;
            runs_by: string;
            schedules: components["schemas"]["ScheduleEntry"][];
            time_zone: string;
        };
        ProjectSettings: {
            attention_label: string;
            /** Format: int64 */
            daily_token_budget: null | number;
            /** Format: int32 */
            fix_rounds_card: number;
            /** Format: int32 */
            fix_rounds_feature: number;
            hold_label: string;
            model_building: string;
            model_reading: string;
            model_reviewing: string;
            pickup_label: string;
            /** @enum {string} */
            preset: "fully_automatic" | "guided" | "only_i_merge" | "custom";
            questions: boolean;
            review_label: string;
            /** Format: int32 */
            run_budget_usd: number;
            /** Format: int32 */
            run_minutes: number;
            /** Format: int32 */
            run_turns: number;
            trusted_logins: components["schemas"]["TrustedLogin"][];
            wait_paths: string[];
            wait_when_decision: boolean;
            wait_when_marked: boolean;
            wait_when_paths: boolean;
            waits_for_owner: string;
        };
        ProjectSetup: {
            can_send_back: boolean;
            check: null | components["schemas"]["SetupCheck"];
            checking: boolean;
            problem: null | string;
            proposal: null | components["schemas"]["SetupProposal"];
            settings_waits: boolean;
            setup: null | components["schemas"]["SetupRun"];
            setup_command: string;
            state: string;
            steps: components["schemas"]["SetupStep"][];
        };
        ProjectSummary: {
            cards: components["schemas"]["CardCounts"];
            feature: null | components["schemas"]["FeatureProgress"];
            setting_up: null | components["schemas"]["SetupProgress"];
            workers: components["schemas"]["WorkersHealth"];
        };
        ProjectValueList: {
            done: boolean;
            values: components["schemas"]["ProjectValueName"][];
        };
        ProjectValueName: {
            asked: boolean;
            name: string;
            problem: null | string;
            /** Format: date-time */
            set_at: null | string;
            why: null | string;
        };
        ProjectValueRequest: {
            value: null | string;
        };
        PushKeyAnswer: {
            /** Format: date-time */
            expires_at: string;
            push_key: string;
        };
        PushKeyRequest: {
            /** Format: uuid */
            worker_id: null | string;
        };
        QuestionCard: {
            feature: null | components["schemas"]["CardFeature"];
            /** Format: uuid */
            id: string;
            /** Format: int32 */
            number: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            questions: components["schemas"]["ReadingQuestion"][];
            /** Format: date-time */
            read_at: string;
            /** Format: uuid */
            reading_id: string;
            title: string;
            /** Format: uri */
            web_url: string;
        };
        ReadingQuestion: {
            question: string;
            recommendation: string;
            why: string;
        };
        RefusedCard: {
            missing: string[];
            /** Format: int32 */
            number: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: date-time */
            refused_at: string;
            title: string;
            /** Format: uri */
            web_url: string;
        };
        Report: {
            decisions: components["schemas"]["ReportDecision"][];
            follow_ups: string[];
            handover: string;
            installed: string[];
            outcome: components["schemas"]["ReportOutcome"];
            pages: components["schemas"]["ReportPage"][];
            replies: components["schemas"]["ReportReply"][];
            stopped_reason: string;
            summary: string;
            tasks: components["schemas"]["ReportTask"][];
            tests: components["schemas"]["ReportTest"][];
        };
        ReportDecision: {
            alternative: string;
            chosen: string;
            for_owner: boolean;
            why: string;
        };
        /** @enum {unknown} */
        ReportOutcome: "done" | "stopped";
        ReportPage: {
            looked: boolean;
            path: string;
            why: string;
        };
        ReportReply: {
            answer: string;
            finding: string;
        };
        ReportTask: {
            done: boolean;
            text: string;
        };
        ReportTest: {
            result: string;
            what: string;
        };
        ResultPicture: {
            name: null | string;
            /** Format: byte */
            png: null | string;
        };
        ResultRequest: {
            auth_method: null | string;
            branch: null | string;
            check: null | components["schemas"]["CheckResult"];
            /** Format: int64 */
            duration_ms: null | number;
            failure: null | components["schemas"]["RunFailure"];
            head_commit: null | string;
            outcome: null | string;
            permission_denials?: unknown;
            pictures?: null | components["schemas"]["ResultPicture"][];
            remerge_diff?: null | string;
            report: null | components["schemas"]["JsonElement"];
            screenshots?: null | components["schemas"]["ResultScreenshots"];
            stderr: null | string;
            usage: null | components["schemas"]["JsonElement"];
            /** Format: uuid */
            worker_id: null | string;
        };
        ResultScreenshots: {
            names: null | string[];
            note: null | string;
        };
        ResumeRequest: {
            commit: null | string;
        };
        ReviewFinding: {
            blocking: boolean;
            file: string;
            /** Format: int32 */
            line: number;
            what: string;
            why: string;
        };
        ReviewOwnerView: {
            decision: string;
            view: string;
        };
        RunCheck: {
            /** Format: int32 */
            exit_code: number;
            output: string;
        };
        RunFailure: {
            in_card: null | boolean;
            reason: null | string;
            /** Format: date-time */
            resets_at?: null | string;
            timed_out?: null | boolean;
        };
        RunLogLine: {
            /** Format: date-time */
            at: string;
            kind: string;
            /** Format: int64 */
            seq: number;
            text: string;
        };
        RunLogPage: {
            lines: components["schemas"]["RunLogLine"][];
            more: boolean;
        };
        RunNowState: {
            on: boolean;
            outside_schedule: boolean;
            something_to_run: boolean;
        };
        RunningList: {
            /** Format: int32 */
            busy_workers: number;
            next: components["schemas"]["NextCard"][];
            /** Format: int32 */
            next_more: number;
            runs: components["schemas"]["RunningRun"][];
        };
        RunningRun: {
            /** Format: int64 */
            cache_reads: number;
            /** Format: int32 */
            card_number: null | number;
            /** Format: int32 */
            feature_number: null | number;
            /** Format: int32 */
            fix_round: null | number;
            kind: string;
            model: null | string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: uuid */
            run_id: string;
            /** Format: date-time */
            started_at: string;
            state: string;
            stopping: boolean;
            title: null | string;
            /** Format: int64 */
            tokens: number;
            /** Format: uuid */
            worker_id: string;
            worker_name: string;
        };
        ScheduleEntry: {
            active: boolean;
            days: string[];
            from: string;
            /** Format: uuid */
            id: string;
            name: string;
            to: string;
        };
        ScheduleRequest: {
            runs_by: null | string;
            schedules: null | components["schemas"]["ScheduleWindowRequest"][];
        };
        ScheduleWindowRequest: {
            active: null | boolean;
            days: null | string[];
            from: null | string;
            /** Format: uuid */
            id: null | string;
            name: null | string;
            to: null | string;
        };
        ScreenshotGroup: {
            images: string[];
            name: string;
        };
        SendBackRequest: {
            comment: null | string;
        };
        SettingsRequest: {
            attention_label: null | string;
            /** Format: int64 */
            daily_token_budget: null | number;
            /** Format: int32 */
            fix_rounds_card: null | number;
            /** Format: int32 */
            fix_rounds_feature: null | number;
            hold_label: null | string;
            model_building: null | string;
            model_reading: null | string;
            model_reviewing: null | string;
            pickup_label: null | string;
            questions: null | boolean;
            review_label: null | string;
            /** Format: double */
            run_budget_usd: null | number;
            /** Format: int32 */
            run_minutes: null | number;
            /** Format: int32 */
            run_turns: null | number;
            trusted_logins: null | string[];
            wait_paths: null | string[];
            wait_when_decision: null | boolean;
            wait_when_marked: null | boolean;
            wait_when_paths: null | boolean;
            waits_for_owner: null | string;
        };
        SetupCheck: {
            again_asked: boolean;
            /** Format: date-time */
            checked_at: string;
            commit: string;
            /** Format: int32 */
            exit_code: number;
            /** Format: uuid */
            id: string;
            output: string;
            passed: boolean;
            pictures: string[];
            pictures_note: null | string;
            version: string;
        };
        SetupMergeRequest: {
            head_commit: null | string;
        };
        /** @enum {unknown} */
        SetupPreset: "fully_automatic" | "guided" | "only_i_merge";
        SetupProgress: {
            /** Format: int32 */
            done: number;
            /** Format: int32 */
            total: number;
        };
        SetupProject: {
            /** Format: date-time */
            checked_at: string;
            /** Format: int32 */
            exit_code: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            state: string;
            version: string;
        };
        SetupProposal: {
            paths: string[];
            preset: components["schemas"]["SetupPreset"];
            why: string;
        };
        SetupReport: {
            decisions: components["schemas"]["ReportDecision"][];
            handover: string;
            proposed: components["schemas"]["SetupProposal"];
            summary: string;
            unsettled: components["schemas"]["SetupUnsettled"][];
            variables: components["schemas"]["SetupVariable"][];
        };
        SetupReview: {
            /** Format: int32 */
            blocking: number;
            commit: string;
            summary: string;
            verdict: string;
        };
        SetupRun: {
            branch: string;
            head_commit: null | string;
            /** Format: int32 */
            number: number;
            /** Format: int32 */
            pull_request_number: null | number;
            pull_request_url: null | string;
            reason: null | string;
            report: null | components["schemas"]["SetupReport"];
            review: null | components["schemas"]["SetupReview"];
            running: boolean;
            sent_back: boolean;
            state: string;
        };
        SetupRunAgainRequest: {
            comment: null | string;
        };
        SetupRunItem: {
            /** Format: date-time */
            changed_at: string;
            head_commit: null | string;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: int32 */
            pull_request_number: null | number;
            /** Format: uri */
            pull_request_url: null | string;
            reason: null | string;
            state: string;
        };
        SetupSendBackRequest: {
            comment: null | string;
        };
        SetupStep: {
            done: boolean;
            step: string;
        };
        SetupUnsettled: {
            detail: string;
            kind: components["schemas"]["SetupUnsettledKind"];
        };
        /** @enum {unknown} */
        SetupUnsettledKind: "real_secret" | "containers_in_tests" | "macos_needed" | "other";
        SetupVariable: {
            name: string;
            why: string;
        };
        SseItemOfRunLogLine: {
            data?: components["schemas"]["RunLogLine"];
            event_id?: null | string;
            event_type?: null | string;
            reconnection_interval?: null | string;
        };
        SseItemOfRunningList: {
            data?: components["schemas"]["RunningList"];
            event_id?: null | string;
            event_type?: null | string;
            reconnection_interval?: null | string;
        };
        StopNowAnswer: {
            /** Format: int32 */
            stopped: number;
        };
        StoppedCard: {
            /** Format: int32 */
            feature_number: null | number;
            /** Format: int32 */
            number: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: uri */
            pull_request_url: null | string;
            reason: string;
            /** Format: date-time */
            stopped_at: string;
            stopped_by_owner: boolean;
            title: string;
            /** Format: uri */
            web_url: string;
        };
        StoppedFeature: {
            /** Format: int32 */
            number: number;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            reason: string;
            state: string;
            /** Format: date-time */
            stopped_at: string;
            title: string;
            /** Format: uri */
            web_url: string;
        };
        /** Format: binary */
        Stream: string;
        TimeZoneAnswer: {
            time_zone: string;
        };
        TimeZoneRequest: {
            time_zone: null | string;
        };
        TrustedLogin: {
            login: string;
            mine: boolean;
        };
        UsageCard: {
            /** Format: int64 */
            agent_ms: number;
            /** Format: int64 */
            cache_reads: number;
            /** Format: int64 */
            elapsed_ms: number;
            /** Format: int32 */
            fix_rounds: number;
            /** Format: int32 */
            number: number;
            /** Format: int32 */
            runs: number;
            /** Format: int32 */
            sent_back: number;
            title: null | string;
            /** Format: int64 */
            tokens: number;
        };
        UsageDay: {
            /** Format: date */
            date: string;
            /** Format: int64 */
            tokens: number;
        };
        UsageFeature: {
            /** Format: int64 */
            agent_ms: number;
            /** Format: int64 */
            cache_reads: number;
            cards: components["schemas"]["UsageCard"][];
            /** Format: int64 */
            elapsed_ms: number;
            /** Format: int32 */
            fix_rounds: number;
            /** Format: int32 */
            number: number;
            own: null | components["schemas"]["UsageFigures"];
            /** Format: int32 */
            runs: number;
            /** Format: int32 */
            sent_back: number;
            title: string;
            /** Format: int64 */
            tokens: number;
        };
        UsageFigures: {
            /** Format: int64 */
            agent_ms: number;
            /** Format: int64 */
            cache_reads: number;
            /** Format: int32 */
            runs: number;
            /** Format: int64 */
            tokens: number;
        };
        UsageProject: {
            /** Format: int64 */
            agent_ms: number;
            /** Format: int64 */
            cache_reads: number;
            cards: components["schemas"]["UsageCard"][];
            days: components["schemas"]["UsageDay"][];
            features: components["schemas"]["UsageFeature"][];
            /** Format: uuid */
            id: string;
            name: string;
            /** Format: int32 */
            runs: number;
            setting_up: null | components["schemas"]["UsageFigures"];
            /** Format: int64 */
            tokens: number;
        };
        UsageView: {
            period: string;
            projects: components["schemas"]["UsageProject"][];
        };
        WaitingItem: {
            can_merge: boolean;
            decisions: components["schemas"]["OwnerDecision"][];
            head_commit: string;
            /** Format: uuid */
            id: string;
            kind: components["schemas"]["WaitingKind"];
            /** Format: int32 */
            number: number;
            /** Format: int32 */
            other_decisions: number;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: date-time */
            read_at: string;
            reason: string;
            reviewer_view: string[];
            screenshots: components["schemas"]["ScreenshotGroup"][];
            sent_back: boolean;
            /** Format: int32 */
            slice_number: null | number;
            summary: string;
            title: string;
            /** Format: uri */
            web_url: string;
        };
        /** @enum {unknown} */
        WaitingKind: "waiting" | "stopped";
        WaitingList: {
            cards: components["schemas"]["CardItem"][];
            /** Format: int32 */
            count: number;
            decisions: components["schemas"]["DecisionItem"][];
            failing_projects: components["schemas"]["FailingProject"][];
            features: components["schemas"]["FeatureItem"][];
            items: components["schemas"]["WaitingItem"][];
            needs_relink: boolean;
            own_items: components["schemas"]["OwnItem"][];
            projects: components["schemas"]["ProjectFreshness"][];
            question_cards: components["schemas"]["QuestionCard"][];
            refused_cards: components["schemas"]["RefusedCard"][];
            setup_projects: components["schemas"]["SetupProject"][];
            setup_runs: components["schemas"]["SetupRunItem"][];
            stopped_cards: components["schemas"]["StoppedCard"][];
            stopped_features: components["schemas"]["StoppedFeature"][];
        };
        WhoAmI: {
            /** Format: uuid */
            account_id: string;
        };
        Worker: {
            base_image: string;
            /** Format: date-time */
            first_seen_at: string;
            /** Format: uuid */
            id: string;
            /** Format: date-time */
            last_seen_at: string;
            name: string;
            rebuild: null | string;
            recipe_version: string;
        };
        WorkerKey: {
            key: string;
            save_command: string;
        };
        WorkerList: {
            budget?: null | components["schemas"]["BudgetHold"];
            cannot_run?: null | string;
            held: null | string;
            /** Format: date-time */
            key_made_at: null | string;
            recipe_version: null | string;
            start_command: string;
            workers: components["schemas"]["Worker"][];
        };
        WorkersHealth: {
            cannot_run: null | string;
            /** Format: int32 */
            count: number;
            held: null | string;
            /** Format: date-time */
            last_seen_at: null | string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    getApiKey: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ApiKeyStatus"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    setApiKey: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ApiKeyRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ApiKeyStatus"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    removeApiKey: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getAttention: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Attention"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    answerCard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AnswersRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getClaudeLogin: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ClaudeLoginStatus"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    setClaudeLogin: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ClaudeLoginRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ClaudeLoginStatus"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    removeClaudeLogin: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    getDisplay: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DisplayData"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    startGitHubLink: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["LinkStarted"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    unlinkGitHub: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    listConnectableRepositories: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Connectable"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    health: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Health"];
                };
            };
        };
    };
    getPaused: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AllPausedState"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    pauseEverything: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PauseRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    listProjects: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    connectRepository: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ConnectRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Project"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    disconnectProject: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    listCards: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CardList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getCard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CardStoryView"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    dropCard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    mergeCard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["MergeRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runCardAgain: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runCardNow: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runCardBySchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getCardScreenshot: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
                name: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "image/png": components["schemas"]["Stream"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    sendBackCard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SendBackRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getFeature: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FeatureStoryView"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    answerDecisionFine: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
                run: string;
                position: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DecisionAnswer"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    sendBackDecision: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
                run: string;
                position: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SendBackRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getFeatureMerge: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FeatureMergeConfirmation"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    mergeFeature: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["MergeRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runFeatureAgain: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runFeatureNow: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runFeatureBySchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    sendBackFeature: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                number: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SendBackRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    pauseProject: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PauseRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    refreshProject: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    resumeProject: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ResumeRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    readRunLog: {
        parameters: {
            query?: {
                after?: number;
            };
            header?: never;
            path: {
                id: string;
                run: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunLogPage"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    streamRunLog: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                run: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/event-stream": components["schemas"]["SseItemOfRunLogLine"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    stopRun: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                run: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getSchedules: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectSchedules"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    replaceSchedules: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ScheduleRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectSchedules"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    getProjectSettings: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectSettings"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    saveProjectSettings: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SettingsRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectSettings"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    getProjectSetup: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectSetup"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    checkProjectAgain: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getSetupPicture: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                check: string;
                name: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "image/png": components["schemas"]["Stream"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    mergeProjectSetup: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SetupMergeRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    runProjectSetupAgain: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SetupRunAgainRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    sendProjectSetupBack: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SetupSendBackRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    stopProjectNow: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StopNowAnswer"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getProjectValues: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProjectValueList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    setProjectValue: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                name: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ProjectValueRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    removeProjectValue: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                name: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    makeWorkerKey: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WorkerKey"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    listWorkers: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WorkerList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    listRunning: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunningList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    streamRunning: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/event-stream": components["schemas"]["SseItemOfRunningList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    status: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DevFlowStatus"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    getTimeZone: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TimeZoneAnswer"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    setTimeZone: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["TimeZoneRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TimeZoneAnswer"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    getUsage: {
        parameters: {
            query?: {
                period?: string;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UsageView"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    listWaiting: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WaitingList"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    mergeWaiting: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["MergeRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    getWaitingScreenshot: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                name: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "image/png": components["schemas"]["Stream"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    sendBackWaiting: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SendBackRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    whoami: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WhoAmI"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    workerHello: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["HelloRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    workerNextJob: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NextJobRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Job"];
                };
            };
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    workerJobLog: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["LogRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["LogAnswer"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    workerJobProgress: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ProgressRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
        };
    };
    workerPushKey: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PushKeyRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PushKeyAnswer"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    workerJobResult: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ResultRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    workerRecipe: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": string;
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
}
