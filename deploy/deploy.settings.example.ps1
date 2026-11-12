# Copy this file to deploy.settings.ps1 (that one is ignored by git) and fill in your values.
# Used by deploy.ps1 (manual deploy), run-local.ps1 (local run) and set-github-secrets.ps1.

# SmarterASP > Publishing methods > VS Webdeploy
$SiteName   = "your-site-name"            # e.g. abc123-001-site1
$UserName   = "your-webdeploy-user"       # e.g. abc123-001
$ServerHost = "winXXXX.site4now.net"      # host part of the Service URL

# SmarterASP > Databases (MSSQL)
$DbServer   = "sqlXXXX.site4now.net"
$DbName     = "db_xxxxxx_chatbot"
$DbUser     = "db_xxxxxx_chatbot_admin"

# Local run only (run-local.bat): the default admin store created on your PC
$SeedSlug     = "riwaq"
$SeedName     = "رِواق"
$SeedEmail    = "admin@riwaq.local"
$SeedPassword = "choose-a-local-password"
