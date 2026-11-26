# SellyChat = Chatwoot's community edition (MIT, github.com/chatwoot/chatwoot; the "-ce" image has no enterprise/ folder)
# with Sellybot's name and icon. Nothing else in Chatwoot is changed, so a new Chatwoot version is a new tag here and a rebuild.
#   - the icons (favicons, home-screen icons) are drawn from brand/icon.svg, the logos are brand/logo*.svg;
#   - the word "Chatwoot" in the dashboard's texts becomes "SellyChat" (only whole words inside the built texts: every
#     place it appears there is a sentence, never code; checked on v4.20.1).
ARG CHATWOOT=v4.20.1-ce
FROM chatwoot/chatwoot:${CHATWOOT}

COPY brand/ /tmp/sellychat/
RUN set -e; cd /app/public; \
    for f in favicon-*.png apple-icon*.png apple-touch-icon*.png android-icon-*.png ms-icon-*.png; do \
      s=$(echo "$f" | grep -oE '[0-9]+x[0-9]+' | cut -dx -f1); [ -n "$s" ] || s=180; \
      magick -background none -density 600 /tmp/sellychat/icon.svg -resize "${s}x${s}" "$f"; \
    done; \
    cp /tmp/sellychat/logo.svg brand-assets/logo.svg; \
    cp /tmp/sellychat/logo-dark.svg brand-assets/logo_dark.svg; \
    cp /tmp/sellychat/icon.svg brand-assets/logo_thumbnail.svg; \
    ruby -i -pe 'gsub(/\bChatwoot\b/, "SellyChat")' manifest.json vite/assets/*.js; \
    rm -rf /tmp/sellychat
