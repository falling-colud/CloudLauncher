#!/bin/sh
# CloudLauncher for Linux: installs this unpacked folder for the current user, or removes it again.
#
#   ./install.sh               copy it to ~/.local/share/cloudlauncher-app and add it to the app menu
#   ./install.sh <folder>      copy it to <folder> instead
#   ./install.sh --uninstall   take it out of the app menu and delete the installed copy
#
# Everything stays in your home folder (XDG_DATA_HOME, else ~/.local/share); nothing needs root. Your
# instances and settings (~/.local/share/CloudLauncher) are never touched. The launcher writes the
# same menu entry itself (Settings > Add to App Menu).
set -eu

# printf, not echo: some shells' echo rewrites backslashes in what it prints.
say() { printf '%s\n' "$*"; }

fail() {
  printf 'install.sh: %s\n' "$*" >&2
  exit 1
}

usage() {
  say "usage: $0 [folder]                 install (default: \$XDG_DATA_HOME/cloudlauncher-app)"
  say "       $0 --uninstall [folder]     remove"
}

data_home=${XDG_DATA_HOME:-}
case $data_home in
  /*) ;;
  *) data_home=$HOME/.local/share ;;
esac
apps=$data_home/applications
desktop=$apps/cloudlauncher.desktop
hicolor=$data_home/icons/hicolor
icon=$hicolor/256x256/apps/cloudlauncher.png

# The files a release folder holds. Uninstalling deletes these and nothing else.
release_files="cloudlauncher install.sh cloudlauncher.png LICENSE.txt THIRD-PARTY-NOTICES.txt README.txt"

mode=install
target=
for arg in "$@"; do
  case $arg in
    --uninstall) mode=uninstall ;;
    -h|--help) usage; exit 0 ;;
    -*) usage >&2; fail "unknown option $arg" ;;
    *) [ -z "$target" ] || fail "give one folder at most"; target=$arg ;;
  esac
done

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
nl='
'

# A desktop entry's Exec argument, quoted: \ " ` $ get a backslash inside the quotes, % is written
# %%, then every backslash is doubled again for the string value (Desktop Entry Specification).
exec_arg() {
  printf '"%s"' "$(printf '%s' "$1" | sed -e 's/[\\"`$]/\\&/g' -e 's/%/%%/g')" | sed -e 's/\\/\\\\/g'
}

# A plain string value: backslashes doubled.
string_value() {
  printf '%s' "$1" | sed -e 's/\\/\\\\/g'
}

refresh_caches() {
  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$apps" >/dev/null 2>&1 || true
  fi
  # Only where an icon cache already exists: a new one would leave other programs' icons out of it.
  if [ -f "$hicolor/icon-theme.cache" ] && command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f "$hicolor" >/dev/null 2>&1 || true
  fi
}

# ── uninstall ────────────────────────────────────────────────────────────────

if [ "$mode" = uninstall ]; then
  if [ -z "$target" ]; then
    # The copy the menu entry starts, else the default place.
    if [ -f "$desktop" ]; then
      entry_exe=$(sed -n 's/^TryExec=//p' "$desktop" | head -n 1 | sed -e 's/\\\\/\\/g')
      if [ -n "$entry_exe" ] && [ -f "$entry_exe" ]; then
        target=$(dirname -- "$entry_exe")
      fi
    fi
    target=${target:-$data_home/cloudlauncher-app}
  fi

  removed=
  if [ -f "$desktop" ]; then rm -f -- "$desktop"; removed=yes; fi
  if [ -f "$icon" ]; then rm -f -- "$icon"; fi
  [ -z "$removed" ] || refresh_caches

  # Only a folder that is plainly a CloudLauncher release (its program, install.sh and icon), so a
  # wrong folder given by hand loses nothing.
  if [ -f "$target/cloudlauncher" ] && [ -f "$target/install.sh" ] && [ -f "$target/cloudlauncher.png" ]; then
    for name in $release_files; do
      rm -f -- "$target/$name"
    done
    rm -rf -- "$target/.cloudlauncher-update"
    if rmdir -- "$target" 2>/dev/null; then
      say "Removed CloudLauncher from $target."
    else
      say "Removed CloudLauncher's files from $target; the other files there were left alone."
    fi
  elif [ -n "$removed" ]; then
    say "Removed CloudLauncher from the app menu."
  else
    say "CloudLauncher is not in the app menu, and $target holds no copy of it."
  fi
  say "Your instances and settings in $data_home/CloudLauncher were left alone."
  exit 0
fi

# ── install ──────────────────────────────────────────────────────────────────

[ -f "$here/cloudlauncher" ] || fail "run this from the unpacked CloudLauncher folder: there is no cloudlauncher program next to it"
target=${target:-$data_home/cloudlauncher-app}
mkdir -p -- "$target"
target=$(CDPATH= cd -- "$target" && pwd -P)
case $target in
  *"$nl"*) fail "the folder's path has a line break in it" ;;
esac

if [ "$target" != "$here" ]; then
  for path in "$here"/*; do
    [ -e "$path" ] || continue
    name=$(basename -- "$path")
    if [ -d "$path" ]; then
      rm -rf -- "$target/$name"
      cp -R -- "$path" "$target/$name"
    else
      # A copy under another name, then a rename: a launcher running from there keeps going, where
      # copying over its program would fail ("Text file busy").
      cp -- "$path" "$target/.$name.new"
      mv -f -- "$target/.$name.new" "$target/$name"
    fi
  done
fi
chmod 755 -- "$target/cloudlauncher"
[ ! -f "$target/install.sh" ] || chmod 755 -- "$target/install.sh"

exe=$target/cloudlauncher
if [ -f "$target/cloudlauncher.png" ]; then
  mkdir -p -- "$(dirname -- "$icon")"
  cp -- "$target/cloudlauncher.png" "$icon.new"
  mv -f -- "$icon.new" "$icon"
fi
mkdir -p -- "$apps"
{
  say "[Desktop Entry]"
  say "Type=Application"
  say "Name=CloudLauncher"
  say "Comment=A cloud-synced Minecraft launcher"
  say "Exec=$(exec_arg "$exe") %U"
  say "TryExec=$(string_value "$exe")"
  say "Icon=cloudlauncher"
  say "Terminal=false"
  say "Categories=Game;"
  say "StartupWMClass=cloudlauncher"
} > "$desktop.new"
chmod 644 -- "$desktop.new"
mv -f -- "$desktop.new" "$desktop"
refresh_caches

say "CloudLauncher is installed in $target and is in your applications menu."
say "Start it from a terminal with: $exe"
say "Remove it again with: $target/install.sh --uninstall"
