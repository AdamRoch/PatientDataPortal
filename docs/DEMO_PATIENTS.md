# Demo patients

Use only these synthetic records for demonstrations. Each record includes completed studies and a signed report.

| Use | Patient ID | Date of birth | Completed studies | Signed reports |
| --- | --- | --- | ---: | ---: |
| Client walkthrough | `SYN-0007` | `08/08/1967` | 5 | 1 |
| Demo video | `SYN-0009` | `10/10/1969` | 4 | 1 |
| Backup | `SYN-0006` | `07/07/1966` | 3 | 1 |

Patient claims are intentionally permanent. After an account verifies one of these identities, another account cannot claim the same patient. Confirm that a record is still unclaimed before giving it to someone.

For a client walkthrough, have the client create and confirm their own account, then give them only the patient ID and date of birth reserved for that walkthrough. Keep a different record available for recorded demos.

The production seed contains 50 synthetic patients. A future repeatable sales-demo workflow should use a dedicated resettable demo environment instead of weakening the one-account-per-patient rule.
