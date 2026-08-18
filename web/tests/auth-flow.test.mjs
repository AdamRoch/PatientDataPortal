import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (file) => readFile(new URL(`../src/${file}`, import.meta.url), "utf8");

test("registration requests Supabase email confirmation and tells the patient to check their inbox", async () => {
  const card = await source("components/auth-card.tsx");

  assert.match(card, /supabase\.auth\.signUp/);
  assert.match(card, /emailRedirectTo: `\$\{window\.location\.origin\}\//);
  assert.match(card, /Check your inbox to confirm your email/);
});

test("the portal requires both a current session and a confirmed email", async () => {
  const portal = await source("app/portal/page.tsx");

  assert.match(portal, /supabase\.auth\.getSession/);
  assert.match(portal, /hasVerifiedEmail\(data\.session\.user\.email_confirmed_at\)/);
  assert.match(portal, /event === "SIGNED_OUT"/);
  assert.match(portal, /reason=session-expired/);
});

test("patient session endpoint rejects a request without a bearer token before reading configuration", async () => {
  const route = await source("app/api/patient/session/route.ts");
  const authGuard = route.indexOf('if (!authorization?.startsWith("Bearer "))');
  const configRead = route.indexOf("process.env.NEXT_PUBLIC_SUPABASE_URL");

  assert.ok(authGuard >= 0);
  assert.ok(configRead > authGuard);
  assert.match(route, /status: 401/);
});

test("profile proxy verifies the patient session and forwards only to the authenticated profile API", async () => {
  const route = await source("app/api/patient/profile/route.ts");
  const profile = await source("components/patient-profile.tsx");

  assert.match(route, /auth\.getUser/);
  assert.match(route, /new URL\("\/api\/profile", apiUrl\)/);
  assert.match(route, /authorization/);
  assert.doesNotMatch(route, /\[userId\]/);
  assert.match(profile, /fetch\("\/api\/patient\/profile"/);
});

test("deletion requests are patient-authenticated and visible only through the admin route", async () => {
  const patientRoute = await source("app/api/patient/deletion-request/route.ts");
  const adminRoute = await source("app/api/admin/deletion-requests/route.ts");
  const profile = await source("components/patient-profile.tsx");
  const viewer = await source("components/deletion-requests-viewer.tsx");
  assert.match(patientRoute, /auth\.getUser/); assert.match(patientRoute, /new URL\("\/api\/deletion-requests", apiUrl\)/);
  assert.match(adminRoute, /auth\.getUser/); assert.match(adminRoute, /new URL\("\/api\/admin\/deletion-requests", apiUrl\)/);
  assert.match(profile, /Request deletion of my data/); assert.match(profile, /\/api\/patient\/deletion-request/);
  assert.match(viewer, /\/api\/admin\/deletion-requests/); assert.match(viewer, /Pending deletion requests/);
});

test("studies proxy and list keep study ownership on the server and show a clean empty state", async () => {
  const route = await source("app/api/patient/studies/route.ts");
  const studies = await source("components/studies-list.tsx");
  const imaging = await source("app/portal/imaging/page.tsx");

  assert.match(route, /auth\.getUser/);
  assert.match(route, /new URL\("\/api\/studies", apiUrl\)/);
  assert.match(route, /cache: "no-store"/);
  assert.doesNotMatch(route, /patientRecordId/);
  assert.match(studies, /fetch\("\/api\/patient\/studies"/);
  assert.match(studies, /No completed studies are available yet\./);
  assert.match(studies, /study\.description/);
  assert.match(studies, /study\.performedAt/);
  assert.match(imaging, /<StudiesList \/>/);
});

test("image viewer asks the server to mint a private short-lived URL and supports accessible reset after expiry", async () => {
  const route = await source("app/api/patient/images/[id]/route.ts");
  const viewer = await source("components/image-viewer.tsx");
  const viewerStyles = await source("components/image-viewer.module.css");
  const studies = await source("components/studies-list.tsx");

  assert.match(route, /auth\.getUser/);
  assert.match(route, /new URL\(`\/api\/images\/\$\{encodeURIComponent\(id\)\}`/);
  assert.match(route, /cache: "no-store"/);
  assert.match(viewer, /fetch\(`\/api\/patient\/images\/\$\{encodeURIComponent\(imageId\)\}`/);
  assert.match(viewer, /onPointerDown/);
  assert.match(viewerStyles, /touch-action: none/);
  assert.match(viewer, /aria-label="Zoom in"/);
  assert.match(viewer, /onError=\{remintAfterExpiredUrl\}/);
  assert.match(studies, /\/portal\/imaging\/\$\{imageId\}/);
});

test("image sharing authenticates the patient before forwarding an image-only share request", async () => {
  const route = await source("app/api/patient/images/[id]/share/route.ts");
  const viewer = await source("components/image-viewer.tsx");
  const styles = await source("components/image-viewer.module.css");

  assert.match(route, /auth\.getUser/);
  assert.match(route, /new URL\("\/api\/share", apiUrl\)/);
  assert.match(route, /resourceType: "image"/);
  assert.match(route, /const \{ id \} = await context\.params/);
  assert.match(route, /cache: "no-store"/);
  assert.match(viewer, /Share image/);
  assert.match(viewer, /\/api\/patient\/images\/\$\{encodeURIComponent\(imageId\)\}\/share/);
  assert.match(viewer, /Recipient email/);
  assert.match(viewer, /Secure link sent\. It expires in 48 hours\./);
  assert.match(viewer, /We could not share this image\. Please try again\./);
  assert.match(styles, /\.shareForm/);
  assert.match(styles, /@media \(max-width: 480px\)/);
});

test("patient file flows provide a route back to the portal or public home page", async () => {
  const navigation = await source("components/portal-navigation.tsx");
  const imaging = await source("app/portal/imaging/page.tsx");
  const image = await source("app/portal/imaging/[id]/page.tsx");
  const cine = await source("app/portal/cine/[id]/page.tsx");
  const reports = await source("app/portal/reports/page.tsx");
  const publicShare = await source("app/s/[token]/page.tsx");

  assert.match(navigation, /href="\/portal"/);
  assert.match(navigation, /Back to portal/);
  for (const page of [imaging, image, cine, reports]) assert.match(page, /<PortalNavigation \/>/);
  assert.match(publicShare, /Patient Data Portal home/);
  assert.match(publicShare, /href="\/"/);
});

test("identity verification keeps care navigation behind verified patient state", async () => {
  const identity = await source("components/identity-verification.tsx");
  const route = await source("app/api/patient/identity/route.ts");
  const styles = await source("components/identity-verification.module.css");

  assert.match(identity, /label htmlFor="patientRef">Patient ID/);
  assert.match(identity, /label htmlFor="dob">Date of birth/);
  assert.match(identity, /type="date"/);
  assert.match(identity, /state === "unlinked"/);
  assert.match(identity, /setState\(verified \? "verified" : "unlinked"\)/);
  assert.match(identity, /href="\/portal\/imaging"/);
  assert.match(identity, /href="\/portal\/reports"/);
  assert.match(identity, /We could not verify your identity\. Please try again later\./);
  assert.match(route, /new URL\(request\.method === "GET" \? "\/api\/identity\/status" : "\/api\/identity\/verify", apiUrl\)/);
  assert.match(route, /auth\.getUser/);
  assert.match(styles, /@media \(max-width: 480px\)/);
  assert.match(styles, /\.form button \{ width: 100%; \}/);
});

test("reports expose signed-only metadata and open PDFs outside a blocked storage iframe", async () => {
  const listRoute = await source("app/api/patient/reports/route.ts");
  const viewRoute = await source("app/api/patient/reports/[reportId]/view/route.ts");
  const shareRoute = await source("app/api/patient/reports/[reportId]/share/route.ts");
  const viewer = await source("components/reports-viewer.tsx");
  const styles = await source("components/reports-viewer.module.css");

  assert.match(listRoute, /auth\.getUser/);
  assert.match(listRoute, /new URL\("\/api\/reports", apiUrl\)/);
  assert.match(viewRoute, /encodeURIComponent\(reportId\)/);
  assert.match(shareRoute, /auth\.getUser/);
  assert.match(shareRoute, /new URL\("\/api\/share", apiUrl\)/);
  assert.match(shareRoute, /resourceType: "report"/);
  assert.match(shareRoute, /cache: "no-store"/);
  assert.match(viewer, /Loading your signed reports/);
  assert.match(viewer, /No signed reports are available yet/);
  assert.match(viewer, /window\.location\.assign\(url\)/);
  assert.doesNotMatch(viewer, /<iframe/);
  assert.match(viewer, /We could not open that report/);
  assert.match(viewer, /\/api\/patient\/reports\/\$\{encodeURIComponent\(reportId\)\}\/share/);
  assert.match(viewer, /Share report/);
  assert.match(viewer, /Recipient email/);
  assert.match(viewer, /Secure link sent\. It expires in 48 hours\./);
  assert.match(styles, /width: 100%/);
  assert.match(styles, /@media \(max-width: 480px\)/);
  assert.doesNotMatch(viewer, /preliminary/);
});

test("email outbox viewer proxies to the server-enforced admin endpoint without rendering payloads", async () => {
  const route = await source("app/api/admin/email-outbox/route.ts");
  const viewer = await source("components/email-outbox-viewer.tsx");
  const styles = await source("components/email-outbox-viewer.module.css");

  assert.match(route, /auth\.getUser/);
  assert.match(route, /new URL\("\/api\/admin\/email-outbox", apiUrl\)/);
  assert.match(route, /cache: "no-store"/);
  assert.match(viewer, /fetch\("\/api\/admin\/email-outbox"/);
  assert.match(viewer, /Provider message ID/);
  assert.match(viewer, /Due/);
  assert.doesNotMatch(viewer, /payload|href=/i);
  assert.match(styles, /@media \(max-width: 480px\)/);
});

test("appointment picker uses the authenticated discovery routes and labels viewer-local times at phone width", async () => {
  const providersRoute = await source("app/api/patient/providers/route.ts");
  const slotsRoute = await source("app/api/patient/providers/[id]/slots/route.ts");
  const picker = await source("components/appointment-picker.tsx");
  const styles = await source("components/appointment-picker.module.css");

  assert.match(providersRoute, /auth\.getUser/);
  assert.match(providersRoute, /new URL\("\/api\/providers", apiUrl\)/);
  assert.match(slotsRoute, /encodeURIComponent\(id\)/);
  assert.match(slotsRoute, /query\.toString\(\)/);
  assert.match(picker, /Choose a provider/);
  assert.match(picker, /Choose a service/);
  assert.match(picker, /Times shown in \{zone\}/);
  assert.match(picker, /Intl\.DateTimeFormat/);
  assert.match(styles, /@media \(max-width: 480px\)/);
});

test("share management uses authenticated patient routes and renders active and historic links at phone width", async () => {
  const listRoute = await source("app/api/patient/shares/route.ts");
  const revokeRoute = await source("app/api/patient/shares/[id]/route.ts");
  const management = await source("components/share-management.tsx");
  const styles = await source("components/share-management.module.css");

  assert.match(listRoute, /auth\.getUser/);
  assert.match(listRoute, /new URL\("\/api\/shares", apiUrl\)/);
  assert.match(revokeRoute, /method: "DELETE"/);
  assert.match(revokeRoute, /encodeURIComponent\(id\)/);
  assert.match(management, /\/api\/patient\/shares/);
  assert.match(management, /Revoke link/);
  assert.match(management, /share\.recipientEmail/);
  assert.match(management, /share\.expiresAt/);
  assert.match(management, /share\.status/);
  assert.match(styles, /@media \(max-width: 480px\)/);
});
