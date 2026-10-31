# Website (duopilo.app)

Static pages, no framework, no JavaScript. English at `/`, German at `/de/`; the legal pages (`impressum.html`, `datenschutz.html`) are German.

| File | Content |
|---|---|
| `index.html`, `de/index.html` | Home page in English and German – keep both in sync |
| `impressum.html`, `datenschutz.html` | Legal notice and privacy policy |
| `404.html` | Not found page (absolute paths – works on the custom domain) |
| `assets/site.css` | The only stylesheet |

Icons, logo and fonts aren't stored here: [`tools/build_site.py`](../tools/build_site.py) copies them from `branding/` and `src/Duopilo/Dashboard/fonts/` and fills in the latest published release (`%%VERSION%%`, `%%SETUP_URL%%`, `%%ZIP_URL%%`, `%%RELEASE_URL%%`, `%%RELEASE_STATE%%`). Without a published release the page shows "coming soon"; a pre-release is shown as beta. Drafts never appear.

## Preview

```bash
python tools/build_site.py --allow-todo      # or --no-release
python -m http.server -d _site 8000          # http://localhost:8000
```

## Publishing

[`.github/workflows/pages.yml`](../.github/workflows/pages.yml) builds and deploys on pushes to `main` that touch the site, branding or fonts, whenever a release is published, edited or deleted, and by hand. Pull requests only build. The build fails on `main` while a page still contains `TODO:` – that's the legal notice.

### Once beforehand

1. **Legal notice:** fill in name, address and e-mail in `impressum.html` (the `TODO:` lines).
2. **Pages:** Settings → Pages → Build and deployment → Source **GitHub Actions**.
3. **Verify the domain** for the organization first (Organization settings → Pages → Add a domain) – this keeps others from using it on GitHub Pages.
4. **Custom domain:** Settings → Pages → Custom domain `duopilo.app`. No `CNAME` file is needed with an Actions workflow.
5. **DNS** at the registrar:

   | Name | Type | Value |
   |---|---|---|
   | `duopilo.app` | A | `185.199.108.153`, `185.199.109.153`, `185.199.110.153`, `185.199.111.153` |
   | `duopilo.app` | AAAA | `2606:50c0:8000::153`, `2606:50c0:8001::153`, `2606:50c0:8002::153`, `2606:50c0:8003::153` |
   | `www` | CNAME | `duopilo.github.io` |

6. **HTTPS:** once the certificate is issued, tick **Enforce HTTPS**. `.app` domains only work over HTTPS anyway (HSTS preload).
7. Run the workflow once by hand (Actions → Pages → Run workflow) or merge a change to `site/`.

`duopilo.com` stays unused for now; it could later redirect at the registrar.
