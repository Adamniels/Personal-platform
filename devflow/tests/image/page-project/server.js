// A small page project, as base-image.test.sh starts one with the commands the developer's brief
// gives: two pages on localhost, answered only when it was started with DEVFLOW=1, as the brief
// says to start a project, so its pictures prove the variable reached it.
const http = require("node:http");

if (process.env.DEVFLOW !== "1") {
	console.error("DEVFLOW is not 1, so the project does not start");
	process.exit(1);
}

const pages = {
	"/": "<h1>Home</h1><p>The page project.</p>",
	"/habits": "<h1>Habits</h1><p>No habits yet. Add one to start.</p>",
};

http
	.createServer((request, response) => {
		const page = pages[request.url];
		response.writeHead(page ? 200 : 404, { "content-type": "text/html" });
		response.end(`<!doctype html><title>Page project</title>${page ?? "<h1>Not found</h1>"}`);
	})
	.listen(4173, () => console.log("listening on 4173 with DEVFLOW=1"));
