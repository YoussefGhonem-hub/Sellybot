# SellyChat: Sellybot's name and logo on Chatwoot (community edition), the super admin, and the Platform App whose
# token Sellybot uses. Safe to run again after every upgrade (setup.sh does): it only sets what's missing or changed.
#   docker compose exec -T -e SC_ADMIN_EMAIL=... -e SC_ADMIN_PASSWORD=... rails bundle exec rails runner - < setup.rb
site = (ENV['SELLYBOT_URL'].presence || 'https://sellybot.online').chomp('/')

# the logos themselves are the image's own /brand-assets files (Dockerfile), which LOGO / LOGO_DARK / LOGO_THUMBNAIL point at already
brand = {
  'INSTALLATION_NAME' => 'SellyChat',
  'BRAND_NAME' => 'SellyChat',
  'BRAND_URL' => site,
  'WIDGET_BRAND_URL' => site,
  'TERMS_URL' => "#{site}/privacy.html",
  'PRIVACY_URL' => "#{site}/privacy.html"
}
brand.each do |name, value|
  row = InstallationConfig.find_by(name: name) || InstallationConfig.new(name: name)
  next if row.persisted? && row.value.to_s == value.to_s

  row.value = value
  row.save!
  puts "set #{name}"
end
GlobalConfig.clear_cache if defined?(GlobalConfig) && GlobalConfig.respond_to?(:clear_cache)

email = ENV['SC_ADMIN_EMAIL'].to_s.strip
if email.present? && !User.exists?(email: email)
  pw = ENV['SC_ADMIN_PASSWORD'].to_s
  abort 'SC_ADMIN_PASSWORD is needed for a new super admin' if pw.blank?
  admin = SuperAdmin.new(email: email, name: 'Sellybot', password: pw, password_confirmation: pw)
  admin.skip_confirmation!
  admin.save!
  puts "super admin #{email} made"
end

# no «Welcome to Chatwoot» first-run page: the super admin above is the installation's owner
::Redis::Alfred.delete(::Redis::Alfred::CHATWOOT_INSTALLATION_ONBOARDING)

app = PlatformApp.find_by(name: 'Sellybot') || PlatformApp.create!(name: 'Sellybot')
puts "PLATFORM_TOKEN=#{app.access_token.token}"
