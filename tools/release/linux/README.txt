CloudLauncher for Linux
=======================

Run it from here:

    ./cloudlauncher

Or install it for your user (an entry in your applications menu, nothing outside your home folder):

    ./install.sh

and remove it again with ./install.sh --uninstall. Your instances and settings live in
~/.local/share/CloudLauncher and are left alone either way.

It needs a 64-bit (x86-64) Linux with a desktop (X11, or Wayland with XWayland), glibc 2.27 or later,
fontconfig and the usual X11 libraries, which every desktop distribution has. Java is downloaded by
the launcher when an instance needs it.

Microsoft sign-in: the launcher shows a code; open microsoft.com/link in any browser, on this
computer or your phone, and enter it.

The launcher keeps itself up to date (Settings > Check for Updates), and checks the signature of
every update before installing it.

https://cloudlauncher.co
