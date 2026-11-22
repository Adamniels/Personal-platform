// Generated from contracts/devflow/openapi.json by tools/generate-schema.mjs, with
// `npm run generate:api`. Do not edit: src/api/schema.test.ts fails when it is stale.

export interface paths {
    "/devflow/api/attention": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Whether something needs the caller, for the nav bar
         * @description Read from what Dev flow stored and never from GitHub. attention is true when the caller has a pull request, waiting or stopped, that is not sent back, a stopped project, a card of Dev flow's whose pull request waits, a card of Dev flow's refused as too thin, a feature of Dev flow's ready for review or waiting for the caller, a GitHub link that has expired, or a Claude login due for renewal within a month or past it, and false otherwise. The nav bar asks it with the session's cookie. Without a valid token, 401 with an empty body.
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
         * @description Read from what Dev flow stored and never from GitHub. waiting, the pull requests that wait for the caller and were not sent back, the runner's and Dev flow's own cards', with Dev flow's own cards refused as too thin and its features ready for review or waiting for the caller; stopped, those the runner stopped on, not sent back, and the caller's projects stopped because something landed on their default branch that Dev flow did not put there; projects, how many the caller has connected; stale, whether any project is stale or failing, as the waiting list says them; needs_relink, whether the caller's GitHub link has expired; and claude_login, where their Claude login stands, none, ok, due_soon or expired, as GET /devflow/api/claude-login says it. The core calls it with the user's token in the Authorization header. Without a valid token, 401 with an empty body.
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
    "/devflow/api/projects": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * The caller's projects
         * @description Each connected repository, by owner and name. Without a valid token, 401 with an empty body; a change from another origin, 403.
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
         * @description The card as last read, its number, title, web_url, state and reason, and its pull_request, number and url, null before one was opened; each of its runs, newest first, with its kind, the worker it was handed to, when it started and finished, how long it took, its result and, for a failure, its reason and whether it lay in the card, the auth method, the tokens in and out, the check's exit code and the last lines of its output, null when it did not run, and the commit it left; the latest report in its form, the latest build's, null when no build has handed one back; and each review, newest first, with the commit the worker reviewed, its verdict, summary, findings, each blocking or not with its file, line, what and why, the reviewer's view on each decision for the owner, when it was recorded, whether its copy is posted, and whether it is current, for the card's head commit; and the screenshots of its head commit, the commit, the pictures' names paired desktop and phone by page, read through getCardScreenshot, and the worker's note, null when its newest build that succeeded tried none. And the feature it is a card of, its number and title, null for a card on its own. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a card the caller does not have. Without a valid token, 401 with an empty body.
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
         * @description The feature as last read, its number, title, web_url, state and reason, null when there is nothing to say; its branch, the name and the commit it was at after the newest change its watch judged, or the one it was made at before any, null before the branch is made; its pull_request to the default branch, number and url, null before one is opened; and its cards in their sub-issue order, each with its number, title, web_url, state, reason, fix_rounds, the input_tokens and output_tokens of all its runs, the screenshots of its head commit as getCard has them, read through getCardScreenshot, and the decisions its latest report marked for the owner, each with the run_id of that report's run, its position among the report's decisions, what was chosen, the alternative, why, the reviewer_view from the newest review of the commit that run left, null without one, and the answer, fine or sent_back, null before one, can_send_back, whether the card has landed in the feature's branch of a feature neither merged nor closed, so it can be sent back, and sent_back, its last send back, how many times, the pull request it had landed in and the owner's comments, null when it was never sent back; its own runs and the reviews of the whole feature, newest first; fix_rounds, the rounds its reviews have started; and rounds, each fix of the whole feature newest first, as getCard has a card's rounds. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a feature the caller does not have. Without a valid token, 401 with an empty body.
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
    "/devflow/api/projects/{id}/settings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * A project's settings
         * @description The labels that pick and hold its cards, mark what needs the caller and mark a card for their review; whether a card's pull request waits for them, never, sometimes or always, with the three conditions of sometimes and the paths it watches; and its trusted accounts, the GitHub logins whose cards, edits and comments count, the caller's linked login first and marked as theirs. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        get: operations["getProjectSettings"];
        /**
         * Change a project's settings
         * @description Takes all ten and answers them as saved. A label is written, at most 50 characters, with no space at either end and no comma, and the four are different names whatever their case; waits_for_owner is never, sometimes or always, and sometimes has at least one of wait_when_marked, wait_when_paths and wait_when_decision; wait_paths is at most 100 glob patterns of at most 200 characters, none starting with ! or /, and a pattern given twice is kept once; trusted_logins is a list of GitHub logins that holds the caller's linked login, and a login given twice whatever its case is kept once. 422 naming each field that breaks a rule, 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
         */
        put: operations["saveProjectSettings"];
        post?: never;
        delete?: never;
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
         * @description Every worker that has said hello with the project's key, by when it first did, with what it reported, when it was last seen, and rebuild: recipe when it runs another version of the recipe than Dev flow last read, base_image when it runs on another base image than the one released, or null; key_made_at, when the project's key was made, null before one has been; recipe_version, the recipe's version as last read, null before then or when the default branch has none; start_command, the command that builds and starts the project's worker; and held, why the project hands out no work whichever worker asks, the first that applies: stopped, when something landed on its default branch that Dev flow did not put there, unwatched, when its default branch has not been read for five minutes, no_claude_login when its user has given no Claude login, claude_login_expired when it is past its renewal, or null; and cannot_run, once six runs in a row have failed outside their cards, "Dev flow cannot run <project>'s cards: <the last reason>", until a run succeeds, or null. The key itself is never in it. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.
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
    "/devflow/api/waiting": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * What waits for the caller, as last read
         * @description Read from what Dev flow stored and never from GitHub. items are the pull requests that are the caller's turn in every project, by project and number; own_items are Dev flow's own, read from its own tables, in this part each a project stopped because something landed on its default branch that Dev flow did not put there, with the commit, who did it, when, and the commit's address on GitHub, by project; cards are Dev flow's own cards whose pull requests wait for the caller, each with its pull request, the head commit its run built and checked, the branch it merges into, the report's summary, the decisions the agent marked for the caller with the count of the others, the check Dev flow ran, review, the reviewer's verdict on its head commit with its summary and how many findings block, null when it has none, and can_merge, false while the project is stopped, by project and number; stopped_cards are Dev flow's own cards that stopped, each with its issue's address, where a comment runs it again, why it stopped, when, and its pull request's address or null, by project and number; refused_cards are Dev flow's own cards refused as too thin to build, each with its issue's address, where an edit or a comment reads it again, what is missing, each a line, and when it was refused, by project and number; failing_projects are the projects whose runs have failed outside their cards six times in a row, each with message, "Dev flow cannot run <project>'s cards: <the last reason>", the reason, how many failures, since when and until when it hands out no job, shown until a run succeeds, by project; decisions are the open issues Dev flow opened for the decisions the agent marked for the caller on cards it merged, each with what was chosen, the alternative, why and the reviewer's view, by project, oldest first, until the caller closes it on GitHub; count is the items not sent back, the own items, the cards, the stopped and refused cards, the failing projects, the decisions and the features, plus one when needs_relink; projects says for each when it was last read, stale when that is more than five minutes ago or never, and failing when its last read failed. Without a valid token, 401 with an empty body.
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
         * @description Marks the worker seen and answers its next job, 200 with Cache-Control: no-store: the first of its project's queued cards that waits for nothing, by priority, high, none, low, then oldest first, moved to building, with a run recorded for this worker. The job holds the card, the repository, the branch, the brief, the report's schema, the check, the run's user's Claude token, a push key narrowed to the repository and its expiry, the committer and the limits. 204 when there is no work for it: a project that is stopped, or whose default branch has not been watched for five minutes, no card to take, a user without a Claude login or with one past its renewal, a worker that needs a rebuild, running another version of the recipe than Dev flow last read or another base image than the one released, or the code host not answering what a job needs, such as the project's .devflow/project.json. 409 for a worker that has not said hello in the key's project, which says hello and asks again; 422 without worker_id. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
         */
        post: operations["workerNextJob"];
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
         * @description What the worker sends when its job ends, whatever happened: outcome, succeeded with the agent's report or failed with the failure's reason and whether it lies in the card; the check's exit code and the last 200 lines of its output, null when it did not run; the branch, which must be the job's, and its head commit; the auth method; Claude's usage as it came, an object; the duration in milliseconds; the last 200 lines of stderr; and permission_denials, the actions the agent was refused, an array or null. Each is stored on the run as it came. A build's report is read against contracts/devflow/report.schema.json, and one that does not match is a failure in the card; a review's report is its verdict, read against contracts/devflow/verdict.schema.json, and one that does not match is a failure in the run, never a verdict. A review's head_commit is the commit it was handed, or null on a failure; any other is 422. A failure outside the card puts the card back in queued, counting nothing against it; a failure in the card, a report that does not match, a report that stopped, a check that did not run or failed stop it with why; otherwise the card waits, checked, for its pull request. 204. Only for the worker holding the job while it runs, and once; 404 otherwise. 422 naming a field that is missing or breaks a rule. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.
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
         * @description The file .devflow/Dockerfile of the key's project, from its repository's default branch, read as the app at the newest commit that changed it: text/plain, with that commit's id in X-Devflow-Recipe-Version, X-Content-Type-Options: nosniff and Cache-Control: no-store. 404 when the repository has none; 422 naming the line when it copies files into the image, a COPY or an ADD in any case, after continued lines are joined, or when it is not UTF-8 text of at most 64 KB; 503 when GitHub did not answer. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body.
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
        Attention: {
            attention: boolean;
        };
        CardCheck: {
            command: null | string;
            commit: string;
            /** Format: int32 */
            exit_code: number;
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
            why: string;
        };
        CardReadingView: {
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
            check: null | components["schemas"]["RunCheck"];
            /** Format: int64 */
            duration_ms: null | number;
            /** Format: date-time */
            finished_at: null | string;
            head_commit: null | string;
            /** Format: uuid */
            id: string;
            in_card: null | boolean;
            /** Format: int64 */
            input_tokens: null | number;
            kind: string;
            /** Format: int64 */
            output_tokens: null | number;
            reason: null | string;
            result: null | string;
            /** Format: date-time */
            started_at: string;
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
            reviews: components["schemas"]["CardReview"][];
            rounds: components["schemas"]["CardRound"][];
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
            can_send_back: boolean;
            decisions: components["schemas"]["FeatureDecision"][];
            /** Format: int32 */
            fix_rounds: number;
            /** Format: int64 */
            input_tokens: number;
            /** Format: int32 */
            number: number;
            /** Format: int64 */
            output_tokens: number;
            reason: null | string;
            screenshots: null | components["schemas"]["CardScreenshots"];
            sent_back: null | components["schemas"]["CardSentBack"];
            state: string;
            title: string;
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
        FeatureStoryView: {
            branch: null | components["schemas"]["FeatureBranch"];
            cards: components["schemas"]["FeatureCard"][];
            /** Format: int32 */
            fix_rounds: number;
            /** Format: int64 */
            input_tokens: number;
            merge: null | components["schemas"]["FeatureMergeOffer"];
            /** Format: int32 */
            most_rounds: number;
            /** Format: int32 */
            number: number;
            /** Format: int64 */
            output_tokens: number;
            pull_request: null | components["schemas"]["CardPullRequest"];
            reason: null | string;
            reviews: components["schemas"]["CardReview"][];
            rounds: components["schemas"]["CardRound"][];
            runs: components["schemas"]["CardRun"][];
            state: string;
            title: string;
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
            card: components["schemas"]["JobCard"];
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
            to_fix?: null | components["schemas"]["JobFix"];
            to_read?: null | components["schemas"]["JobRead"];
            to_resolve?: null | components["schemas"]["JobConflict"];
            to_review?: null | components["schemas"]["JobReview"];
            to_review_feature?: null | components["schemas"]["JobFeatureReview"];
            /** @default  */
            url: string;
        };
        JobCard: {
            body: string;
            /** Format: int32 */
            number: number;
            title: string;
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
        JsonElement: unknown;
        LinkStarted: {
            /** Format: uri */
            authorize_url: string;
        };
        MergeRequest: {
            head_commit: null | string;
        };
        NextJobRequest: {
            /** Format: uuid */
            worker_id: null | string;
        };
        OwnItem: {
            activity_type: string;
            actor: null | string;
            branch: string;
            commit: string;
            /** Format: uri */
            commit_url: string;
            kind: components["schemas"]["OwnItemKind"];
            /** Format: date-time */
            landed_at: string;
            owner: string;
            /** Format: uuid */
            project_id: string;
            project_name: string;
            /** Format: date-time */
            stopped_at: string;
        };
        /** @enum {unknown} */
        OwnItemKind: "project_stopped";
        OwnerDecision: {
            alternative: null | string;
            chosen: string;
            why: null | string;
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
            projects: components["schemas"]["Project"][];
        };
        ProjectSettings: {
            attention_label: string;
            /** Format: int32 */
            fix_rounds_card: number;
            /** Format: int32 */
            fix_rounds_feature: number;
            hold_label: string;
            model_building: string;
            model_reading: string;
            model_reviewing: string;
            pickup_label: string;
            review_label: string;
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
        PushKeyAnswer: {
            /** Format: date-time */
            expires_at: string;
            push_key: string;
        };
        PushKeyRequest: {
            /** Format: uuid */
            worker_id: null | string;
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
        ScreenshotGroup: {
            images: string[];
            name: string;
        };
        SendBackRequest: {
            comment: null | string;
        };
        SettingsRequest: {
            attention_label: null | string;
            /** Format: int32 */
            fix_rounds_card: null | number;
            /** Format: int32 */
            fix_rounds_feature: null | number;
            hold_label: null | string;
            model_building: null | string;
            model_reading: null | string;
            model_reviewing: null | string;
            pickup_label: null | string;
            review_label: null | string;
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
        StoppedCard: {
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
            title: string;
            /** Format: uri */
            web_url: string;
        };
        /** Format: binary */
        Stream: string;
        TrustedLogin: {
            login: string;
            mine: boolean;
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
            refused_cards: components["schemas"]["RefusedCard"][];
            stopped_cards: components["schemas"]["StoppedCard"][];
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
            cannot_run?: null | string;
            held: null | string;
            /** Format: date-time */
            key_made_at: null | string;
            recipe_version: null | string;
            start_command: string;
            workers: components["schemas"]["Worker"][];
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
