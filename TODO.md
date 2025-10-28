# YellowFox Development TODO

## Phase 1: MVP (Profiles CRUD + Browser Launch) ✅ COMPLETED

- [x] Project structure setup
- [x] Database service with SQLite
- [x] Profile model with fingerprint configuration
- [x] Python CamouFox server script
- [x] Browser service for process management
- [x] Profiles list view with CRUD operations
- [x] Start/Stop browser functionality
- [x] Profile cloning
- [x] Search/filter profiles
- [x] Status indicators

## Phase 2: Proxy Management 🔄 NEXT

### Database Schema
```sql
CREATE TABLE proxies (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    type TEXT NOT NULL,  -- 'socks5', 'http', 'https'
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    username TEXT,
    password TEXT
);

-- Add to profiles table:
ALTER TABLE profiles ADD COLUMN proxy_id TEXT REFERENCES proxies(id);
```

### Features to Implement
- [ ] Proxy model (Models/Proxy.cs)
- [ ] Proxy CRUD in DatabaseService
- [ ] ProxiesView with list management
- [ ] ProxiesViewModel
- [ ] Link proxy to profile in ProfileEditor
- [ ] Pass proxy config to CamouFox in BrowserService
- [ ] Test proxy connection before starting browser
- [ ] Import/export proxy lists (CSV/TXT)

### UI Design
```
Proxies View:
┌─────────────────────────────────────────────────┐
│ [+ Add Proxy] [🔍 Search] [📥 Import]           │
├─────────────────────────────────────────────────┤
│ Name      Type     Host            Port  Status │
│ Proxy-1   SOCKS5   91.132.126.71   8000  ✅     │
│ Proxy-2   HTTP     91.132.126.43   8000  ✅     │
│ Proxy-3   SOCKS5   91.132.126.254  8000  ❌     │
└─────────────────────────────────────────────────┘
```

## Phase 3: Extensions Management 📦 PLANNED

### Database Schema
```sql
CREATE TABLE extensions (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    path TEXT NOT NULL,  -- relative to data/extensions/{id}/
    enabled BOOLEAN DEFAULT true
);
```

### Features to Implement
- [ ] Extension model
- [ ] Extension storage in data/extensions/
- [ ] ExtensionsView for management
- [ ] Load all enabled extensions to CamouFox
- [ ] Extension enable/disable toggle
- [ ] Extension upload (drag & drop .xpi or unpacked folder)
- [ ] Extension deletion with cleanup

### Directory Structure
```
data/
├── extensions/
│   ├── ublock-origin/
│   │   ├── manifest.json
│   │   └── ...
│   ├── extension-2/
│   └── extension-3/
```

## Phase 4: Bookmarks Management 🔖 PLANNED

### Database Schema
```sql
CREATE TABLE bookmarks (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    bookmarks_json TEXT NOT NULL  -- nested JSON structure
);
```

### Features to Implement
- [ ] Bookmarks model
- [ ] BookmarksView with tree/folder structure
- [ ] Add/edit/delete bookmarks
- [ ] Add/edit/delete folders
- [ ] Drag & drop reordering
- [ ] Import from browser (HTML/JSON)
- [ ] Export bookmarks
- [ ] Inject bookmarks into browser profile on start

### Bookmarks JSON Structure
```json
{
  "bookmarks": [
    {
      "type": "folder",
      "name": "Work",
      "children": [
        {"title": "Gmail", "url": "https://gmail.com"},
        {"title": "Alert", "url": "javascript:alert('test')"}
      ]
    },
    {"title": "YouTube", "url": "https://youtube.com"}
  ]
}
```

## Future Enhancements 💡

### Profile Features
- [ ] Profile templates/presets
- [ ] Profile groups/tags
- [ ] Profile import/export (JSON)
- [ ] Bulk operations (start/stop multiple)
- [ ] Profile activity logs
- [ ] Last used timestamp tracking
- [ ] Profile notes with rich text

### Browser Features
- [ ] Open specific URL on start
- [ ] Auto-close after X minutes
- [ ] Browser screenshot capture
- [ ] Page automation scripts
- [ ] Session recording
- [ ] Cookie management
- [ ] Local storage inspection

### Advanced Fingerprinting
- [ ] Manual fingerprint override (advanced mode)
- [ ] Custom User-Agent strings
- [ ] WebGL vendor/renderer selection
- [ ] Canvas fingerprint configuration
- [ ] Audio context spoofing
- [ ] Timezone override per profile
- [ ] Language/locale per profile
- [ ] Custom fonts per profile

### UI Improvements
- [ ] Dark/Light theme toggle
- [ ] Profile cards view (alternative to grid)
- [ ] Keyboard shortcuts
- [ ] Context menus (right-click)
- [ ] Status bar with stats
- [ ] Settings dialog
- [ ] About dialog
- [ ] Update notifications

### System Features
- [ ] Multi-user support with auth
- [ ] Cloud sync for profiles
- [ ] Team sharing features
- [ ] Profile encryption
- [ ] Backup/restore functionality
- [ ] Logging system
- [ ] Performance monitoring
- [ ] Resource usage display (RAM/CPU per profile)

### Developer Features
- [ ] REST API for external control
- [ ] CLI for automation
- [ ] Plugin system
- [ ] Scripting support (Python/JS)
- [ ] Webhook integrations
- [ ] Export analytics

## Technical Debt & Improvements

### Code Quality
- [ ] Add unit tests (xUnit)
- [ ] Add integration tests
- [ ] Error handling improvements
- [ ] Logging framework (Serilog)
- [ ] Configuration system (appsettings.json)
- [ ] Dependency injection
- [ ] Input validation
- [ ] Async/await best practices review

### Performance
- [ ] Database connection pooling
- [ ] Lazy loading for large profile lists
- [ ] Background profile cleanup
- [ ] Memory leak detection
- [ ] Process management improvements
- [ ] Startup time optimization

### Security
- [ ] Encrypt sensitive data (passwords, proxies)
- [ ] Secure credential storage
- [ ] Profile data encryption at rest
- [ ] Process isolation improvements
- [ ] Sandbox browser processes

### Documentation
- [ ] API documentation
- [ ] Architecture diagrams
- [ ] Contributing guidelines
- [ ] Code comments
- [ ] Video tutorials
- [ ] FAQ document

## Known Issues 🐛

- [ ] Python path detection on Linux/macOS
- [ ] Dialog system not implemented (ProfileEditor)
- [ ] No confirmation dialogs for delete operations
- [ ] Browser process orphaning on crash
- [ ] No error messages shown to user
- [ ] Profile edit doesn't reload UI

## Testing Checklist

### Manual Testing
- [ ] Create profile with each OS type
- [ ] Test all screen resolution presets
- [ ] Start multiple profiles simultaneously
- [ ] Stop profiles in different order
- [ ] Clone profile and verify independence
- [ ] Search profiles by name
- [ ] Delete profile with running browser
- [ ] Close app with running browsers
- [ ] Restart app and check profile persistence

### Platform Testing
- [ ] Windows 10/11
- [ ] macOS (Intel)
- [ ] macOS (Apple Silicon)
- [ ] Ubuntu Linux
- [ ] Fedora Linux

### Browser Testing
- [ ] Verify fingerprint uniqueness
- [ ] Test on bot detection sites
- [ ] Check profile isolation
- [ ] Verify data persistence between sessions
- [ ] Test with different websites

## Release Checklist

### v0.1.0 (MVP)
- [x] Basic profile management
- [x] Browser launch/stop
- [x] SQLite database
- [ ] Windows installer
- [ ] Documentation

### v0.2.0 (Proxy Support)
- [ ] Full proxy management
- [ ] Proxy testing
- [ ] Import/export proxies

### v0.3.0 (Extensions)
- [ ] Extension management
- [ ] Extension loading

### v0.4.0 (Bookmarks)
- [ ] Bookmarks management
- [ ] Import/export bookmarks

### v1.0.0 (Stable Release)
- [ ] All Phase 1-4 features
- [ ] Comprehensive testing
- [ ] User documentation
- [ ] Installers for all platforms
