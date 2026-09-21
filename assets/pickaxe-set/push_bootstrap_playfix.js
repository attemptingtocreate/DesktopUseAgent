const fs = require("fs");
const net = require("net");
const crypto = require("crypto");
const source = fs.readFileSync(
  "C:/Users/Administrator/AppData/Local/Cursor/AgentStores/cursor_agent_stores/5015263a-09ef-4037-bba1-ff26f3af5055/files/GameBootstrap.lua",
  "utf8"
);
const id = crypto.randomUUID().replace(/-/g, "");
const req = {
  id,
  method: "roblox.set_script_source",
  params: {
    sessionId: "ea5963d9-af9a-4848-a1ba-26a486b1d7f8",
    instanceId: "inst_2",
    source,
  },
};
const s = net.createConnection("\\\\.\\pipe\\semantic-desktop-agent");
let buf = "";
const t = setTimeout(() => {
  console.error("timeout");
  process.exit(1);
}, 120000);
s.on("connect", () => s.write(JSON.stringify(req) + "\n"));
s.on("data", (d) => {
  buf += d.toString();
  if (buf.includes("\n")) {
    clearTimeout(t);
    console.log(buf.trim().slice(0, 600));
    s.end();
    process.exit(0);
  }
});
s.on("error", (e) => {
  console.error(e);
  process.exit(1);
});
