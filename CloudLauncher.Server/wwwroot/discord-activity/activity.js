// The CloudLauncher Activity: the live board viewer running inside Discord, opened from the
// "Open board" button on a posted board. Discord says which Activity instance this is; the server
// asks Discord where that instance was opened and answers with the boards linked to that channel.

import { DiscordSDK } from "./discord-sdk.js";
import { start } from "./plans/viewer.js?v=3";

function show(title, text) {
  const div = document.createElement("div");
  div.className = "message";
  const strong = document.createElement("strong");
  strong.textContent = title;
  const p = document.createElement("div");
  p.textContent = text;
  div.append(strong, p);
  document.getElementById("stage").append(div);
}

async function main() {
  const config = await fetch("discord/activity/config", { cache: "no-store" }).then((r) => (r.ok ? r.json() : null));
  if (!config?.clientId) return show("Not available", "This server has no Discord app set up.");

  let sdk;
  try {
    sdk = new DiscordSDK(config.clientId);
  } catch {
    return show("Open this from Discord", "It's the board button on a planning board posted in a channel.");
  }
  await sdk.ready();

  const res = await fetch("discord/activity/session", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ instanceId: sdk.instanceId })
  });
  if (!res.ok) return show("Couldn't open the boards", "Close the Activity and open it again from a board's button.");
  const { links } = await res.json();
  if (!links?.length)
    return show("No boards here", "No CloudLauncher pack is linked to this channel. A moderator can link one with /plan link.");
  start({ token: links[0].token, board: links[0].boardId ?? null });
}

main().catch(() => show("Something went wrong", "Close the Activity and open it again."));
