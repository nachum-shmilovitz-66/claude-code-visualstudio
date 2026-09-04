"use strict";
// The setup banner: CLI missing, signed out, and the "a newer CLI is available" reminder.
//
// The reminder is the half of the update check that lives in the page. The host re-checks hourly
// and flags that message `periodic: true`; that flag is what undoes an earlier Dismiss, so a
// snoozed reminder comes back about an hour later instead of being muted for the session. Both
// halves have to work — the host timer was silently dying on a panel hide, and nothing here would
// have caught it, so this pins the page's side of the contract.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

const OUTDATED = { cliFound: true, loggedIn: true, cliVersion: "2.1.258", latestCliVersion: "2.1.260", cliOutdated: true };
const CURRENT = { cliFound: true, loggedIn: true, cliVersion: "2.1.260", latestCliVersion: "2.1.260", cliOutdated: false };

const banner = (app) => app.$("setupBanner");
const visible = (app) => !banner(app).classList.contains("hidden");
const text = (app) => banner(app).textContent;
const action = (app, act) => banner(app).querySelector('[data-act="' + act + '"]');

describe("setup banner — CLI health", () => {
  it("stays out of the way when everything is current", () => {
    const app = boot();
    app.pushMessage("setup", CURRENT);
    assert.strictEqual(visible(app), false);
    assert.strictEqual(banner(app).innerHTML, "");
  });

  it("offers an install when the CLI is missing and npm is there", () => {
    const app = boot();
    app.pushMessage("setup", { cliFound: false, npmFound: true });
    assert.ok(visible(app));
    assert.ok(text(app).includes("Claude CLI not found"));
    assert.ok(action(app, "install"), "npm present should offer a one-click install");
    assert.strictEqual(action(app, "node"), null);
  });

  it("points at Node.js when npm is missing too", () => {
    const app = boot();
    app.pushMessage("setup", { cliFound: false, npmFound: false });
    assert.ok(action(app, "node"), "without npm the only useful action is getting Node");
    assert.strictEqual(action(app, "install"), null);
  });

  it("asks for a sign-in when the CLI reports signed out", () => {
    const app = boot();
    app.pushMessage("setup", { cliFound: true, loggedIn: false });
    assert.ok(visible(app));
    assert.ok(text(app).includes("Not signed in"));
    assert.ok(action(app, "startLogin"), "signing in happens in the panel");
  });
});

describe("setup banner — CLI update reminder", () => {
  it("names both versions so it is obvious what would change", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);

    assert.ok(visible(app));
    const t = text(app);
    assert.ok(t.includes("Claude CLI update available"), t);
    assert.ok(t.includes("2.1.258"), "should name the installed version");
    assert.ok(t.includes("2.1.260"), "should name the available version");
    assert.ok(action(app, "update") && action(app, "recheck") && action(app, "dismiss"));
  });

  it("Update CLI asks the host to update, once", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);

    const btn = action(app, "update");
    btn.click();

    assert.strictEqual(app.sent("updateCli").length, 1);
    assert.strictEqual(btn.disabled, true, "a second click would start a second update");
    assert.strictEqual(btn.textContent, "updating…");
  });

  it("Re-check asks the host to look again", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);

    action(app, "recheck").click();

    assert.strictEqual(app.sent("recheckSetup").length, 1);
  });

  it("Dismiss hides it and it stays hidden for load-time re-reports", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);
    action(app, "dismiss").click();
    assert.strictEqual(visible(app), false);

    // The same news arriving again (a re-render, a solution reload) must not re-nag.
    app.pushMessage("setup", OUTDATED);
    assert.strictEqual(visible(app), false, "a dismissed version should stay dismissed");
  });

  it("the hourly re-check brings a dismissed reminder back", () => {
    // Dismiss is a snooze, not a mute. This is the contract the host's hourly timer relies on.
    const app = boot();
    app.pushMessage("setup", OUTDATED);
    action(app, "dismiss").click();
    assert.strictEqual(visible(app), false);

    app.pushMessage("setup", Object.assign({}, OUTDATED, { periodic: true }));

    assert.strictEqual(visible(app), true, "the hourly re-check should un-snooze the reminder");
    assert.ok(text(app).includes("2.1.260"));
  });

  it("a newer release than the dismissed one shows straight away", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);
    action(app, "dismiss").click();

    app.pushMessage("setup", Object.assign({}, OUTDATED, { latestCliVersion: "2.1.261" }));

    assert.strictEqual(visible(app), true, "a release the user never saw is not covered by the dismiss");
    assert.ok(text(app).includes("2.1.261"));
  });

  it("clears itself once the CLI actually catches up", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);
    assert.ok(visible(app));

    app.pushMessage("setup", CURRENT);

    assert.strictEqual(visible(app), false);
  });

  it("reports the update landing, then stops mentioning it", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);
    action(app, "update").click();

    // The host reports the new version once the background update finishes.
    app.pushMessage("setup", CURRENT);

    assert.ok(visible(app), "the result of an update the user started is worth one banner");
    assert.ok(text(app).includes("updated to"), text(app));
    assert.ok(action(app, "dismissUpdated"));

    action(app, "dismissUpdated").click();
    assert.strictEqual(visible(app), false);
  });

  it("says so when a background update failed", () => {
    const app = boot();
    app.pushMessage("setup", OUTDATED);
    app.pushMessage("cliUpdate", { state: "failed", detail: "EACCES: permission denied" });

    const t = text(app);
    assert.ok(t.includes("EACCES: permission denied"), t);
    assert.ok(action(app, "terminal"), "a failed update should offer a terminal to see it fail in");
  });
});
