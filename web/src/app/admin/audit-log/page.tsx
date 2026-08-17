import { AuditLogViewer } from "@/components/audit-log-viewer";

export default function AuditLogPage() {
  return <main><h1>Audit log</h1><p>Security events show references only. Patient names and clinical details are not displayed.</p><AuditLogViewer /></main>;
}
