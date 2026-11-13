const fs = require("node:fs");
const lock = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
const sdk = lock.packages["node_modules/@oh-my-pi/pi-coding-agent"];
if (!sdk || typeof sdk.version !== "string" || typeof sdk.integrity !== "string") throw new Error("SDK identity missing from package lock");
process.stdout.write(JSON.stringify({ version: sdk.version, integrity: sdk.integrity }));
