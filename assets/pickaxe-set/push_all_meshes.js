const fs = require("fs");
const dir = "C:/Users/Administrator/Desktop/DesktopUseAgent/assets/pickaxe-set";

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

const pkg = JSON.parse(fs.readFileSync(`${dir}/UpgradeSign.mesh.json`, "utf8"));
const src =
  "-- UpgradeSign board (text via SurfaceGui)\nreturn " + toLua(pkg) + "\n";
fs.writeFileSync(`${dir}/UpgradeSign.MeshPackage.lua`, src);
console.log("upgrade bytes", Buffer.byteLength(src), "preview", JSON.stringify(src.slice(0, 70)));

const net = require("net");
const crypto = require("crypto");

function push(instanceId, file) {
  return new Promise((resolve, reject) => {
    const source = fs.readFileSync(file, "utf8");
    const id = crypto.randomUUID().replace(/-/g, "");
    const req = {
      id,
      method: "roblox.set_script_source",
      params: {
        sessionId: "a66ab96c-40ae-4073-a474-065dd75b35e3",
        instanceId,
        source,
      },
    };
    const s = net.createConnection("\\\\.\\pipe\\semantic-desktop-agent");
    let buf = "";
    const t = setTimeout(() => reject(new Error("timeout " + instanceId)), 120000);
    s.on("connect", () => s.write(JSON.stringify(req) + "\n"));
    s.on("data", (d) => {
      buf += d.toString();
      if (buf.includes("\n")) {
        clearTimeout(t);
        console.log(instanceId, buf.trim().slice(0, 400));
        s.end();
        resolve();
      }
    });
    s.on("error", reject);
  });
}

(async () => {
  await push(
    "inst_57",
    `${dir}/MeshPackages.MeshPackage.lua`
  );
  await push("inst_56", `${dir}/UpgradeSign.MeshPackage.lua`);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
