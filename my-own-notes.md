# This is just a place for me to write down some personal notes

## Goal:

Regarding AI I will have one goal/rule and that is to ONLY use AI to write things that I could have written myself. If I can't then it is probably something there that I don't 100% understand. I want to grow with this project and really understand everything about it, especially if this is going to be something that I will always have beside me.

## Notes:

- Question 22/9: I would like to really understand how to work with my system, how do i actually close sub-issues if a branch is for a issue above? can a commit close a sub-issue as well? maybe it can?
  answer 22/9: It works like this, I create a branch for the other issue, then in that branch I work through the leaves and by hand moves the issues along the board. When that slice then is done I create a pr which will close the other issue

- To move an item on the board: gh project item-edit 5 --owner Adamniels --url https://github.com/Adamniels/personal-platform/issues/10 --field "Status" --value "In progress"
- To close: gh issue close issue_nr --reason completed --comment "message"

## Ticket #13
  1. ~~Ignore target/. Add it to the root .gitignore before anything triggers a build.~~
  2. ~~Rename the package. Change name = "Core" in Core/Cargo.toml to something like platform-core. Only the package name changes, the folder stays Core/.~~
  3. ~~Add the dependencies. Use cargo add from inside Core/:~~
     - ~~axum~~
     - ~~tokio with the features macros, rt-multi-thread and net~~
     - ~~dotenvy~~
     ~~Afterwards, look at what cargo add wrote into Cargo.toml and check you understand each line.~~
  4. ~~Declare the variable. Add CORE_LISTEN_ADDR= to Core/.env.example, with no value, since the example only lists names. Then create your own untracked Core/.env with CORE_LISTEN_ADDR=127.0.0.1:8080.~~
  5. ~~Make main async. Put tokio's runtime on main. The thing to understand here is what the attribute generates, and why Rust needs a runtime to run async code at all.~~
  6. ~~Load .env and read the address. Call dotenvy, then read CORE_LISTEN_ADDR and fail fast with a clear message if it's missing. Think about the order these two happen in, and about which errors should crash the program and which you'd handle.~~
  7. ~~Write the handler. An async function that returns plain text.~~
  8. ~~Build the router. A Router with that handler on /.~~
  9. ~~Bind and serve. Bind a tokio TcpListener to the address and hand it and the router to axum::serve. Consider what should happen if binding fails, for example when the port is already taken.~~
  10. ~~Verify. cargo run, then curl the address and check the text comes back. Also run it once without CORE_LISTEN_ADDR set, to confirm it fails the way you intended.~~
  11. Run the checks. cargo fmt, then cargo clippy, and fix any warnings.
