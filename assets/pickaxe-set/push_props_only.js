const fs = require("fs");
const net = require("net");
const crypto = require("crypto");
const dir = "C:/Users/Administrator/Desktop/DesktopUseAgent/assets/pickaxe-set";
const SESSION = "ea5963d9-af9a-4848-a1ba-26a486b1d7f8";

function toLua(v) {
  if (v === null) return "nil";
  if (typeof v === "number") return String(v);
  if (typeof v === "boolean") return v ? "true" : "false";
  if (typeof v === "string") return JSON.stringify(v);
  if (Array.isArray(v)) return "{" + v.map(toLua).join(",") + "}";
  return (
    "{" +
    Object.keys(v)
      .map((k) => "[" + JSON.stringify(k) + "]=" + toLua(v[k]))
      .join(",") +
    "}"
  );
}

const names = [
  "RollButtonSystem",
  "SideButton",
  "PickaxePedestal",
  "RefineSign",
  "UpgradeSign",
];
const packages = {};
for (const n of names) {
  packages[n] = JSON.parse(fs.readFileSync(`${dir}/${n}.mesh.json`, "utf8"));
}
const src = "-- Props only\nreturn " + toLua(packages) + "\n";
fs.writeFileSync(`${dir}/PropsOnly.MeshPackage.lua`, src);
console.log("bytes", Buffer.byteLength(src), "preview", JSON.stringify(src.slice(0, 30)));

function push(instanceId, source) {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID().replace(/-/g, "");
    const req = {
      id,
      method: "roblox.set_script_source",
      params: { sessionId: SESSION, instanceId, source },
    };
    const s = net.createConnection("\\\\.\\pipe\\semantic-desktop-agent");
    let buf = "";
    const t = setTimeout(() => reject(new Error("timeout")), 120000);
    s.on("connect", () => s.write(JSON.stringify(req) + "\n"));
    s.on("data", (d) => {
      buf += d.toString();
      if (buf.includes("\n")) {
        clearTimeout(t);
        console.log(buf.trim().slice(0, 500));
        s.end();
        resolve();
      }
    });
    s.on("error", reject);
  });
}

push("inst_1", src).catch((e) => {
  console.error(e);
  process.exit(1);
});
