import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "ThaiIdCardAgent Web Integration",
  description: "Secure local smart card Agent integration sample",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
