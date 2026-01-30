# Microsoft Store Publishing Guide for Control Room

## Overview

This guide walks through publishing Control Room to the Microsoft Store. The application is configured as a MAUI desktop app packaged for Windows 10+.

---

## Prerequisites

### Required Software
- **Visual Studio 2022** (Community or higher) with MAUI workload
- **Windows 10/11** (21H2 or later recommended)
- **.NET 10 SDK** (already configured)
- **Windows App Certification Kit** (included with Visual Studio)

### Developer Account
- **Microsoft Developer Account** ($19 one-time fee)
- Access to [Partner Center](https://partner.microsoft.com/dashboard)

### Project Configuration
- ✅ Package identity configured (MikeyFrilot.ControlRoom)
- ✅ Publisher name set to CN=mikeyfrilot
- ✅ MSIX packaging enabled in project file
- ✅ Application version: 1.0.0

---

## Step 1: Create a Microsoft Developer Account

### Process
1. Visit [Microsoft Developer Account signup](https://developer.microsoft.com/en-us/store/register)
2. Sign in with Microsoft account (create one if needed)
3. Fill out developer profile information
4. Pay $19 registration fee
5. Wait for account verification (usually 24-48 hours)

### What You'll Get
- Partner Center access
- Developer identity certificate
- App submission capabilities

---

## Step 2: Register Your App in Partner Center

### Access Partner Center
1. Go to https://partner.microsoft.com/dashboard
2. Sign in with your Developer Account
3. Navigate to **Windows & Xbox** → **Apps and games**

### Create New App Submission
1. Click **Create a new product** → **Windows app**
2. Enter app name: `Control Room`
3. Select product type: **Desktop application**
4. Choose availability regions (select all for maximum reach)
5. Select categories:
   - Primary: **Developer Tools**
   - Secondary: **Utilities**
6. Enter rating age: **12+**
7. Click **Create**

### App Identity Information
Partner Center will generate:
- **Package Family Name**: MikeyFrilot.ControlRoom_xxxx...
- **Package/Product ID**: (automatically assigned)
- **Publisher ID**: (automatically assigned)

**Important**: Keep these values for later reference.

---

## Step 3: Prepare Your Application

### Update Application Metadata

Review and update [ControlRoom.App\ControlRoom.App.csproj](ControlRoom.App\ControlRoom.App.csproj):

```xml
<PropertyGroup>
    <!-- Display name for Store -->
    <ApplicationTitle>Control Room</ApplicationTitle>
    
    <!-- Unique identifier -->
    <ApplicationId>com.mikeyfrilot.controlroom</ApplicationId>
    
    <!-- Current version -->
    <ApplicationDisplayVersion>1.0.0</ApplicationDisplayVersion>
    <ApplicationVersion>1</ApplicationVersion>
    
    <!-- Microsoft Store configuration -->
    <WindowsPackageType>Msix</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <WindowsPackageIdentityName>MikeyFrilot.ControlRoom</WindowsPackageIdentityName>
    <WindowsPackagePublisherName>CN=mikeyfrilot</WindowsPackagePublisherName>
    <WindowsStoreCompatible>true</WindowsStoreCompatible>
</PropertyGroup>
```

### Ensure All Resources Are Present

Required app assets:
- ✅ Application icon (128x128 minimum, 500x500 recommended)
- ✅ Splash screen (from Resources/Splash/splash.svg)
- ✅ Screenshots (3-9 screenshots, 1080x1920 minimum)
- ✅ Description text

Assets location: `ControlRoom.App/Resources/`

---

## Step 4: Build the MSIX Package

### Create Self-Signed Certificate (First Time Only)

```powershell
# Create a new certificate
$cert = New-SelfSignedCertificate -CertStoreLocation "cert:\CurrentUser\My" `
    -Subject "CN=mikeyfrilot" `
    -FriendlyName "ControlRoom Store Certificate" `
    -NotAfter (Get-Date).AddYears(5)

# Export certificate as .pfx
$pwd = ConvertTo-SecureString -String "YourPasswordHere" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "ControlRoom_Certificate.pfx" -Password $pwd
```

**Save the .pfx file** to your project root and **remember the password**.

### Build MSIX Package

```powershell
# Navigate to project
cd C:\workspace\control-room

# Build and create MSIX package (Windows only)
dotnet publish -f net10.0-windows10.0.19041.0 `
    -c Release `
    -o .\PublishOutput\

# Or use Visual Studio:
# Right-click ControlRoom.App project → Publish
# Select "Create app packages"
# Choose "Microsoft Store"
```

### Output Files
The build will create:
- `ControlRoom.msix` - Single package file
- `ControlRoom.msixbundle` - For multiple architectures
- `AppxManifest.xml` - Package manifest
- Various supporting files

**Location**: `PublishOutput/` directory

---

## Step 5: Test the Package Locally

### Test Installation

```powershell
# Add certificate to trusted store
Import-PfxCertificate -FilePath "ControlRoom_Certificate.pfx" `
    -CertStoreLocation "cert:\LocalMachine\Root" `
    -Password (ConvertTo-SecureString "YourPasswordHere" -AsPlainText -Force)

# Install the MSIX package
Add-AppxPackage -Path "PublishOutput\ControlRoom.msix"
```

### Verify Installation
- App appears in Start Menu as "Control Room"
- Can be launched successfully
- No crashes or errors during operation
- Uninstalls cleanly via Settings → Apps

---

## Step 6: Run Windows App Certification Kit

### Purpose
The Windows App Certification Kit verifies:
- MSIX package integrity
- Windows Store compatibility
- Security requirements
- Performance baseline

### Run Certification

```powershell
# Windows App Certification Kit location
$CertKitPath = "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\"

# Run validation
& "$CertKitPath\appcert.exe" test -appxpackagepath "PublishOutput\ControlRoom.msix" `
    -reportoutputpath "CertificationReport.xml"
```

### Review Report
- Check `CertificationReport.xml` for any failures
- Address any critical issues before submission
- Common issues: unsigned binaries, platform version, manifest validation

---

## Step 7: Prepare Submission Content

### Store Listing Information

In **Partner Center**, fill out the app listing:

#### Description
```
Control Room is a professional-grade desktop application for managing 
and executing PowerShell scripts with a modern, intuitive interface.

Features:
• Script management and organization
• Execution with real-time output monitoring
• Error handling and logging
• Dark/Light theme support
• Local-first architecture (no cloud dependency)
• MIT open source license

Requires: Windows 10 (Build 19041) or later
```

#### Screenshots (Required: 3-9)
1. Main window showing script list
2. Script execution view with output
3. Settings/configuration screen
4. Dark theme variant
5. Error handling example

Resolution: 1080x1920 or 3840x2160 (landscape orientation)

#### Keywords (up to 7)
- PowerShell script manager
- Script executor
- Desktop automation
- DevOps tools
- Command runner
- Script organization
- Windows utilities

#### Age Rating
- **IARC** (International Age Rating Coalition)
- Select: **12+** or **Parental Guidance**

#### System Requirements
- **Minimum**: Windows 10 Build 19041, 100 MB disk space, 4 GB RAM
- **Recommended**: Windows 11, 500 MB disk space, 8 GB RAM

---

## Step 8: Create Developer Identity Certificate

### Request Certificate for Store Signing

If using Partner Center's recommended approach:

1. In Partner Center → Your App → **Packaging**
2. Download the certificate template: `appxupdate.cer`
3. Use Visual Studio's packaging wizard (handles automatically)

### Alternative: Use Your Self-Signed Certificate

Use the .pfx certificate created in Step 4.

---

## Step 9: Submit to Microsoft Store

### Pre-Submission Checklist
- [ ] All metadata filled in Partner Center
- [ ] Screenshots uploaded (3+ minimum)
- [ ] Description and keywords finalized
- [ ] Privacy policy URL entered (see below)
- [ ] Version number matches (1.0.0)
- [ ] MSIX package built and certified
- [ ] No certification kit errors

### Add Privacy Policy

1. Create [control-room/PRIVACY.md](../PRIVACY.md) or host online
2. In Partner Center: Paste or link privacy policy
3. Microsoft requires explicit privacy information

### Submit Package

1. Partner Center → Your App → **Packages**
2. Click **Upload package**
3. Select your `ControlRoom.msix` or `ControlRoom.msixbundle`
4. Wait for processing (usually 5-10 minutes)
5. Review validation results

### Submission for Review

1. Partner Center → **Submission**
2. Review all information one final time
3. Click **Submit for review**
4. Microsoft reviews (typically 1-3 days)

### Review Process
- Microsoft's automated tools scan for malware/security issues
- Manual review checks Store policies compliance
- You'll receive email notifications of approval/rejection

---

## Step 10: Post-Publication

### After Approval ✅

1. **App appears in Microsoft Store** (may take a few hours to sync)
2. **Generate Store link**: 
   ```
   https://www.microsoft.com/store/apps/9XXXXXXXXXXXXXX
   ```

3. **Enable updates**: Set up CI/CD pipeline to automatically build and submit new versions

### Marketing Your Store Listing
- Update GitHub README with Store link
- Announce on social media
- Add Store badge to website
- Include in press releases

### Version Updates

For future releases:
1. Increment `ApplicationVersion` in .csproj
2. Rebuild MSIX package
3. Rerun Windows App Certification Kit
4. Submit new package through Partner Center
5. Microsoft reviews and publishes update

---

## Troubleshooting

### Common Issues

#### Issue: "Certificate not found"
**Solution**: 
- Install the .pfx certificate: `Import-PfxCertificate`
- Verify certificate is in Personal store
- Check certificate password is correct

#### Issue: "MSIX validation errors"
**Solution**:
- Verify all app assets exist
- Check ApplicationTitle doesn't exceed 256 characters
- Ensure no special characters in package name

#### Issue: "Package rejected by Store"
**Common reasons**:
- Manifest syntax error → Fix AppxManifest.xml
- Privacy policy missing → Add privacy policy
- Unsupported dependencies → Check library versions
- Malware detected → Run Windows Defender full scan

**Contact Microsoft Store Support**: partner.microsoft.com/dashboard → Help

#### Issue: "Installation fails on user machine"
**Solution**:
- Ensure your certificate is installed for target users
- Test on clean Windows 10/11 machine
- Check Windows App Certification Kit report

---

## PowerShell Quick Reference

### Build MSIX
```powershell
cd C:\workspace\control-room
dotnet publish -f net10.0-windows10.0.19041.0 -c Release -o .\PublishOutput\
```

### Create Self-Signed Certificate
```powershell
$cert = New-SelfSignedCertificate -CertStoreLocation "cert:\CurrentUser\My" `
    -Subject "CN=mikeyfrilot" -NotAfter (Get-Date).AddYears(5)
Export-PfxCertificate -Cert $cert -FilePath "ControlRoom_Certificate.pfx" `
    -Password (ConvertTo-SecureString "password" -AsPlainText -Force)
```

### Import Certificate
```powershell
Import-PfxCertificate -FilePath "ControlRoom_Certificate.pfx" `
    -CertStoreLocation "cert:\LocalMachine\Root"
```

### Install MSIX
```powershell
Add-AppxPackage -Path "PublishOutput\ControlRoom.msix"
```

### Run Certification Kit
```powershell
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" `
    test -appxpackagepath "PublishOutput\ControlRoom.msix" `
    -reportoutputpath "CertificationReport.xml"
```

---

## Timeline

| Step | Time | Notes |
|------|------|-------|
| Developer Account | 24-48h | One-time registration |
| Register App (Partner Center) | 15 min | Quick process |
| Build MSIX | 5-10 min | First build takes longer |
| Certification Testing | 2-5 min | Automated |
| Submit for Review | 1-3 days | Microsoft's review time |
| Publication | 1-2h | After approval |
| **Total (First Release)** | **2-4 days** | Mostly waiting for Microsoft |
| **Future Updates** | **Same as above** | Repeat from Build step |

---

## Resources

### Official Documentation
- [MAUI Packaging for Windows](https://learn.microsoft.com/en-us/dotnet/maui/windows/packaging)
- [Microsoft Store Policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
- [MSIX Documentation](https://learn.microsoft.com/en-us/windows/msix/)
- [Partner Center Help](https://partner.microsoft.com/en-us/support)

### Tools
- [Windows App Certification Kit Docs](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit)
- [Visual Studio Packaging Wizard](https://learn.microsoft.com/en-us/visualstudio/deployment/quickstart-deploy-to-windows)

### Examples
- [Official MAUI Store App Examples](https://github.com/dotnet/maui-samples)

---

## Next Steps

1. **Create Microsoft Developer Account** (if not already done)
2. **Generate certificate** and save securely
3. **Build first MSIX package** locally
4. **Test on clean Windows machine**
5. **Register app in Partner Center**
6. **Submit for Microsoft Store review**
7. **Monitor approval process** (email updates)
8. **Launch marketing campaign** upon approval

---

## Support & Questions

- **Microsoft Store Issues**: partner.microsoft.com/dashboard → Help
- **Technical Issues**: GitHub Issues on Control Room repo
- **MAUI Questions**: github.com/dotnet/maui → Discussions

---

**Status**: ✅ Ready to publish to Microsoft Store

**Last Updated**: January 29, 2026

**App Name**: Control Room  
**Package**: MikeyFrilot.ControlRoom  
**Version**: 1.0.0  
**Target**: Windows 10+ (Build 19041+)
