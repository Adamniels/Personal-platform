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
         * @description Read from what Dev flow stored and never from GitHub. attention is true when the caller has a pull request, waiting or stopped, that is not sent back, or a GitHub link that has expired, and false otherwise. The nav bar asks it with the session's cookie. Without a valid token, 401 with an empty body.
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
    "/devflow/api/display": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * What waits, for the front page
         * @description Read from what Dev flow stored and never from GitHub. waiting, the pull requests that wait for the caller and were not sent back; stopped, those the runner stopped on, not sent back; projects, how many the caller has connected; stale, whether any project is stale or failing, as the waiting list says them; and needs_relink, whether the caller's GitHub link has expired. The core calls it with the user's token in the Authorization header. Without a valid token, 401 with an empty body.
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
         * @description Read from what Dev flow stored and never from GitHub. items are the pull requests that are the caller's turn in every project, by project and number; count is those not sent back, plus one when needs_relink; projects says for each when it was last read, stale when that is more than five minutes ago or never, and failing when its last read failed. Without a valid token, 401 with an empty body.
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
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        Attention: {
            attention: boolean;
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
        DevFlowStatus: {
            code_host: components["schemas"]["CodeHostMode"];
            github: components["schemas"]["GitHubLinkStatus"];
        };
        DisplayData: {
            needs_relink: boolean;
            /** Format: int32 */
            projects: number;
            stale: boolean;
            /** Format: int32 */
            stopped: number;
            /** Format: int32 */
            waiting: number;
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
        LinkStarted: {
            /** Format: uri */
            authorize_url: string;
        };
        MergeRequest: {
            head_commit: null | string;
        };
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
        ScreenshotGroup: {
            images: string[];
            name: string;
        };
        SendBackRequest: {
            comment: null | string;
        };
        /** Format: binary */
        Stream: string;
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
            /** Format: int32 */
            count: number;
            items: components["schemas"]["WaitingItem"][];
            needs_relink: boolean;
            projects: components["schemas"]["ProjectFreshness"][];
        };
        WhoAmI: {
            /** Format: uuid */
            account_id: string;
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
}
