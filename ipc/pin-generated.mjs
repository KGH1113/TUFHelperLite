import fs from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";

const directory = fileURLToPath(new URL(".", import.meta.url));
const root = path.dirname(directory), output = path.join(directory, "generated/typescript");
const digest = contents => createHash("sha256").update(contents).digest("hex");
const source = await fs.readFile(path.join(directory, "main.tsp"));
const code = await fs.readFile(path.join(output, "index.js"), "utf8");
const contractHash = code.match(/"hash":\s*"([a-f0-9]{64})"/)?.[1];
if (!contractHash) throw new Error("Generated IPC contract hash is missing.");
const files = {};
for (const name of [".adofai-ipc.generated.json", "index.ts", "index.js", "index.d.ts", "package.json"])
  files[name] = digest(await fs.readFile(path.join(output, name)));
const metadata = JSON.stringify({ source: "tufhelper2/ipc/main.tsp", contractId: "tufhelperlite", contractHash,
  contractSourceSha256: digest(source), generatorVersion: "2.0.0", files }, null, 2) + "\n";
const metadataPath = path.join(output, "source.json");
if (process.argv.includes("--check")) {
  if (await fs.readFile(metadataPath, "utf8") !== metadata) throw new Error("IPC vendor metadata is stale.");
} else await fs.writeFile(metadataPath, metadata);
const inventory = ["ipc/main.tsp", "ipc/adofai-ipc.config.json", "TUFHelperLite/Presentation/Ipc/Generated/TufHelper.g.cs",
  ...Object.keys(files).map(file => "ipc/generated/typescript/" + file), "ipc/generated/typescript/source.json",
  "vendor/adofai-ipc-typespec/adofai-ipc-typespec-2.0.0.tgz"];
const sums = (await Promise.all(inventory.sort().map(async file => `${digest(await fs.readFile(path.join(root, file)))}  ${file}`))).join("\n") + "\n";
const sumsPath = path.join(directory, "SHA256SUMS");
if (process.argv.includes("--check")) {
  if (await fs.readFile(sumsPath, "utf8") !== sums) throw new Error("IPC source/generated checksums are stale.");
} else await fs.writeFile(sumsPath, sums);
console.log(`Pinned TUFHelper contract ${contractHash}`);
