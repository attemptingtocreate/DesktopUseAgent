const fs = require("fs");
const net = require("net");
const crypto = require("crypto");
const source = fs.readFileSync(
  "C:/Users/Administrator/Desktop/DesktopUseAgent/assets/pickaxe-set/UpgradeSign.MeshPackage.lua",
  "utf8"
);
const id = crypto.randomUUID().replace(/-/g, "");
const req = {
  id,
  method: "roblox.set_script_source",
  params: {
    sessionId: "a66ab96c-40ae-4073-a474-065dd75b35e3",
    instanceId: "inst_58",
    source,
  },
};
const s = net.createConnection("\\\\.\\pipe\\semantic-desktop-agent");
let buf = "";
const t = setTimeout(() => {
  console.error("timeout");
  process.exit(1);
}, 60000);
s.on("connect", () => s.write(JSON.stringify(req) + "\n"));
s.on("data", (d) => {
  buf += d.toString();
  if (buf.includes("\n")) {
    clearTimeout(t);
    console.log(buf.trim().slice(0, 500));
    s.end();
    process.exit(0);
  }
});
s.on("error", (e) => {
  console.error(e);
  process.exit(1);
});
