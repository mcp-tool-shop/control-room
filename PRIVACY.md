# Privacy Policy

**Control Room** - Last Updated: January 29, 2026

## Overview

Control Room is committed to protecting your privacy. This Privacy Policy explains how we handle information when you use Control Room.

---

## Data Collection

### What We Collect

**Control Room is a LOCAL-FIRST application.** We collect:

#### ✅ Data We DO NOT Collect
- ❌ No usage analytics or telemetry
- ❌ No personal identification data
- ❌ No script content or execution logs (stored locally only)
- ❌ No device identifiers or hardware info
- ❌ No network requests to external servers
- ❌ No advertising or tracking

#### ✅ Data Stored Locally
All data remains on your machine:
- Script metadata (names, descriptions, paths)
- User preferences and settings
- Script execution history (local database)
- Application configuration

---

## Data Usage

### How We Use Your Data

1. **Local Storage Only**: All application data stays on your computer
2. **No Transmission**: We never send data outside your machine
3. **No Sharing**: We never share data with third parties
4. **User Control**: You control all data retention and deletion

### Exceptions

**Required System Permissions**:
- File system access (reading/writing scripts)
- Process execution (running PowerShell scripts)
- Application settings storage (Windows AppData)

---

## Third-Party Services

### Integrations

**Microsoft Store**:
- App installation and updates (Windows Store only)
- Crash reporting (optional, user consent required)

**No other third-party integrations**.

---

## Data Security

### Protection Measures

- ✅ All local data encrypted at rest (Windows Encryption)
- ✅ Script execution runs in isolated processes
- ✅ No network connections for operation
- ✅ Open source code auditable on GitHub
- ✅ Regular security updates

### Your Responsibility

- Keep your Windows installation updated
- Use strong Windows account credentials
- Secure your machine against unauthorized access
- Review scripts before execution

---

## Data Retention

### How Long We Keep Data

- **Application Settings**: Indefinitely (until you delete the app)
- **Script Files**: Indefinitely (until you delete them)
- **Execution Logs**: Indefinitely (local database)
- **No cloud backup**: Data is never synced to cloud

### Deletion

To delete all Control Room data:

```powershell
# Remove application from Settings → Apps → Apps & features

# Delete local application data:
Remove-Item -Path "$env:APPDATA\ControlRoom" -Recurse
```

---

## Children's Privacy

Control Room is designed for developers and system administrators (age 18+). We do not knowingly collect data from children under 13.

If you believe a child under 13 has used this app, contact us immediately at privacy@mikeyfrilot.com.

---

## Your Rights

### Data Access
- All your data is stored locally on your machine
- You have full access and control
- No hidden data transmission

### Data Modification
- Modify scripts and settings anytime
- Delete data anytime through the application
- Reset to default state anytime

### Data Deletion
- Delete app through Windows Settings
- Delete local data as described above
- No recovery option (data is permanently deleted)

### GDPR / CCPA Compliance
- No personal data collection = no privacy concerns
- No cross-device tracking
- No data sharing with advertisers

---

## Changes to This Policy

We may update this Privacy Policy as Control Room evolves. Changes will be:
1. Posted to this page with updated date
2. Clearly marked as new or modified
3. Effective immediately upon posting

**No notification required** because this app operates entirely locally with no user accounts.

---

## Contact

### Privacy Questions?

Email: privacy@mikeyfrilot.com

### Report Security Concerns

See [SECURITY.md](SECURITY.md) for responsible disclosure procedures.

---

## Summary

| Aspect | Status |
|--------|--------|
| **Data Collection** | ❌ None (local-first design) |
| **Data Transmission** | ❌ None |
| **Analytics** | ❌ None |
| **Advertising** | ❌ None |
| **User Tracking** | ❌ None |
| **Third-party Sharing** | ❌ None |
| **User Control** | ✅ Full |
| **Data Deletion** | ✅ Available |
| **Open Source** | ✅ Fully auditable |
| **Local Encryption** | ✅ Windows-managed |

---

## Applicable Laws

This Privacy Policy complies with:
- ✅ GDPR (EU General Data Protection Regulation)
- ✅ CCPA (California Consumer Privacy Act)
- ✅ LGPD (Lei Geral de Proteção de Dados - Brazil)
- ✅ PIPEDA (Canadian Personal Information Protection Act)

---

**Control Room is committed to privacy by design.**

No user data. No cloud. No analytics. Just a powerful local script manager.

---

*Last Updated: January 29, 2026*  
*Effective Immediately*
