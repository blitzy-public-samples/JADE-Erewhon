// Root component of the React app: the browser router, the session provider, the load-data gate shown while no company
// exists, and the route table of the shop area under /shop and the admin area under /admin.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { JSX, ReactNode } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { AdminLogonPage } from './admin/AdminLogonPage';
import { AdminShell } from './admin/AdminShell';
import { ErrorOutcomeError, api } from './api/client';
import type { LogonArea, LogonOptionsResponse } from './api/types';
import { MessageBox, errorOutcomeOptions } from './components/MessageBox';
import type { MessageBoxOptions } from './components/MessageBox';
import { SessionProvider, useSession } from './session/SessionProvider';
import { LoadDataPrompt } from './setup/LoadDataPrompt';
import { CheckoutPage } from './shop/CheckoutPage';
import { ConfirmationPage } from './shop/ConfirmationPage';
import { LogonPage } from './shop/LogonPage';
import { ShopPage } from './shop/ShopPage';

const SHOP_PATH = '/shop';
const ADMIN_PATH = '/admin';
const LOGON_OPTIONS_PATH = '/api/app/logon-options?area=';

/**
 * Step of the data gate: the logon-options request still unanswered, data present, no data present, the load-data
 * prompt cancelled, or the request failed with an error outcome shown in a message box.
 */
type GateState =
  | { status: 'checking' }
  | { status: 'ready' }
  | { status: 'needsData' }
  | { status: 'closed' }
  | { status: 'failed'; options: MessageBoxOptions };

const CHECKING: GateState = { status: 'checking' };
const READY: GateState = { status: 'ready' };
const NEEDS_DATA: GateState = { status: 'needsData' };
const CLOSED: GateState = { status: 'closed' };

/** Returns `'admin'` when `pathname` starts with `/admin`, otherwise `'shop'`. */
function areaOf(pathname: string): LogonArea {
  return pathname.startsWith(ADMIN_PATH) ? 'admin' : 'shop';
}

/**
 * Returns the gate step after a rejected logon-options request: the message box of the error outcome for an
 * {@link ErrorOutcomeError}, otherwise `ready`.
 */
function failureState(error: unknown): GateState {
  if (error instanceof ErrorOutcomeError) {
    return { status: 'failed', options: errorOutcomeOptions(error.outcome) };
  }
  return READY;
}

/**
 * Renders `children` directly while a session exists. With no session, it reads
 * `GET /api/app/logon-options?area=<area>` on mount and each time the session becomes null, with `admin` as the area
 * under `/admin` and `shop` elsewhere, and renders:
 * - an empty busy `<main>` until the first answer arrives;
 * - `children` once `dataLoaded` is true, and while a later check is still unanswered;
 * - the load-data prompt in place of `children` once `dataLoaded` is false; a completed load checks again, and a
 *   cancelled prompt leaves an empty `<main>` labelled `closed` with no logon until the page is reloaded;
 * - the message box of an error outcome the check failed with, and `children` once it is closed or after any other
 *   failure.
 */
function DataGate({ children }: { children: ReactNode }): JSX.Element {
  const { session } = useSession();
  const { pathname } = useLocation();
  const hasSession = session !== null;
  const [state, setState] = useState<GateState>(() => (hasSession ? READY : CHECKING));
  const [checkNumber, setCheckNumber] = useState(0);
  const pathnameRef = useRef(pathname);

  useEffect(() => {
    pathnameRef.current = pathname;
  }, [pathname]);

  useEffect(() => {
    if (hasSession) {
      return undefined;
    }

    let current = true;
    api.get<LogonOptionsResponse>(LOGON_OPTIONS_PATH + areaOf(pathnameRef.current)).then(
      (options) => {
        if (current) {
          setState(options.dataLoaded ? READY : NEEDS_DATA);
        }
      },
      (error: unknown) => {
        if (current) {
          setState(failureState(error));
        }
      },
    );

    return () => {
      current = false;
    };
  }, [hasSession, checkNumber]);

  const handleLoaded = useCallback((): void => {
    setState(CHECKING);
    setCheckNumber((value) => value + 1);
  }, []);

  const handleCancelled = useCallback((): void => {
    setState(CLOSED);
  }, []);

  const handleFailureClosed = useCallback((): void => {
    setState(READY);
  }, []);

  if (hasSession) {
    return <>{children}</>;
  }

  switch (state.status) {
    case 'checking':
      return <main aria-busy="true" />;
    case 'ready':
      return <>{children}</>;
    case 'needsData':
      return (
        <main>
          <LoadDataPrompt onLoaded={handleLoaded} onCancelled={handleCancelled} />
        </main>
      );
    case 'closed':
      return <main aria-label="closed" />;
    case 'failed':
      return (
        <main>
          <MessageBox {...state.options} onClose={handleFailureClosed} />
        </main>
      );
  }
}

/** Renders the shop page with a shop-area session, otherwise the shop logon page. */
function ShopHome(): JSX.Element {
  const { session } = useSession();
  return session?.area === 'shop' ? <ShopPage /> : <LogonPage />;
}

/** Renders `children` with a shop-area session, otherwise redirects to `/shop`. */
function ShopSessionRoute({ children }: { children: JSX.Element }): JSX.Element {
  const { session } = useSession();
  return session?.area === 'shop' ? children : <Navigate to={SHOP_PATH} replace />;
}

/** Renders the admin shell with an admin-area session, otherwise the admin logon page. */
function AdminHome(): JSX.Element {
  const { session } = useSession();
  return session?.area === 'admin' ? <AdminShell /> : <AdminLogonPage />;
}

/** Renders the admin shell with an admin-area session, otherwise redirects to `/admin`. */
function AdminSessionRoute(): JSX.Element {
  const { session } = useSession();
  return session?.area === 'admin' ? <AdminShell /> : <Navigate to={ADMIN_PATH} replace />;
}

/**
 * The React app. Routes:
 * - `/` redirects to `/shop`;
 * - `/shop` shows the shop page with a shop session, otherwise the shop logon page;
 * - `/shop/checkout` and `/shop/confirmation` show the checkout and confirmation pages with a shop session, otherwise
 *   redirect to `/shop`; the confirmation page reads its receipt from the location state;
 * - any other path under `/shop` redirects to `/shop`;
 * - `/admin` shows the admin shell with an admin session, otherwise the admin logon page;
 * - any other path under `/admin` shows the admin shell with an admin session, otherwise redirects to `/admin`;
 * - every other path redirects to `/shop`.
 */
export function App(): JSX.Element {
  return (
    <BrowserRouter>
      <SessionProvider>
        <DataGate>
          <Routes>
            <Route path="/" element={<Navigate to={SHOP_PATH} replace />} />
            <Route path={SHOP_PATH}>
              <Route index element={<ShopHome />} />
              <Route
                path="checkout"
                element={
                  <ShopSessionRoute>
                    <CheckoutPage />
                  </ShopSessionRoute>
                }
              />
              <Route
                path="confirmation"
                element={
                  <ShopSessionRoute>
                    <ConfirmationPage />
                  </ShopSessionRoute>
                }
              />
              <Route path="*" element={<Navigate to={SHOP_PATH} replace />} />
            </Route>
            <Route path={ADMIN_PATH}>
              <Route index element={<AdminHome />} />
              <Route path="*" element={<AdminSessionRoute />} />
            </Route>
            <Route path="*" element={<Navigate to={SHOP_PATH} replace />} />
          </Routes>
        </DataGate>
      </SessionProvider>
    </BrowserRouter>
  );
}
