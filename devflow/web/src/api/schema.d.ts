// Generated from contracts/devflow/openapi.json by tools/generate-schema.mjs, with
// `npm run generate:api`. Do not edit: src/api/schema.test.ts fails when it is stale.

export interface paths {
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
        ProjectList: {
            projects: components["schemas"]["Project"][];
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
