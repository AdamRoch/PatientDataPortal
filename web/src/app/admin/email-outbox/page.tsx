import { EmailOutboxViewer } from "@/components/email-outbox-viewer";

export default function EmailOutboxPage() {
  return <main><h1>Email outbox</h1><p>Delivery status for queued share and reminder emails.</p><EmailOutboxViewer /></main>;
}
