# The commands the developer's brief gives for the page project, page-project/ beside this file, in
# job 00000000-0000-4000-8000-000000000347, with its page /habits named habits: start it with
# DEVFLOW=1 in the background and wait for it, take the page at a desktop's size and a phone's, and
# stop it. BriefTests.TheImageTestRunsTheBriefsCommands keeps this file the brief's text, and
# TheAgentCanStartTheProjectAndTakePictures in base-image.test.sh runs it as agent, from the copy.
# Everything after these comments is the brief's, unchanged.

cat > /work/00000000-0000-4000-8000-000000000347/self/start.sh <<'DEVFLOW_START'
node server.js
DEVFLOW_START
DEVFLOW=1 setsid sh /work/00000000-0000-4000-8000-000000000347/self/start.sh > /work/00000000-0000-4000-8000-000000000347/self/start.log 2>&1 < /dev/null &
echo $! > /work/00000000-0000-4000-8000-000000000347/self/start.pid
for _ in $(seq 1 180); do
  node -e 'fetch(process.argv[1], { redirect: "manual" }).then((r) => process.exit(r.status < 500 ? 0 : 1), () => process.exit(1))' 'http://localhost:4173' && break
  sleep 1
done
npx playwright screenshot --full-page --viewport-size=1280,800 http://localhost:4173/habits /work/00000000-0000-4000-8000-000000000347/self/habits-desktop.png
npx playwright screenshot --full-page --viewport-size=390,844 http://localhost:4173/habits /work/00000000-0000-4000-8000-000000000347/self/habits-phone.png
kill -- -"$(cat /work/00000000-0000-4000-8000-000000000347/self/start.pid)"
