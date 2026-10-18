/// <reference types="vite/client" />

/** The app version, from package.json (set at build time). */
declare const __APP_VERSION__: string;

interface ImportMetaEnv {
  /** The Microsoft Entra application (client) ID for OneDrive sync. Empty: sync isn't offered. */
  readonly VITE_MS_CLIENT_ID?: string;
  /** Sign-in authority; defaults to https://login.microsoftonline.com/common. */
  readonly VITE_MS_AUTHORITY?: string;
}
