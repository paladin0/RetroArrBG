import React, { useState } from 'react';
import apiClient from '../api/client';

type Candidate = { title: string; size: number; indexerName: string; requiresReview: boolean };
type Item = { file: string; title: string; status: string; size: number; defaultSelected: boolean };
type Preview = { reviewToken: string; items: Item[]; total: number; newGames: number; catalogued: number; ambiguous: number };
const errorMessage = (error: unknown) => {
  if (typeof error === 'object' && error && 'response' in error) {
    const response = (error as { response?: { data?: { error?: string } } }).response;
    if (response?.data?.error) return response.data.error;
  }
  return error instanceof Error ? error.message : String(error);
};

export default function CollectionAcquisition() {
  const [platformId, setPlatformId] = useState(42);
  const [collections, setCollections] = useState<Candidate[]>([]);
  const [folder, setFolder] = useState('');
  const [archive, setArchive] = useState('');
  const [preview, setPreview] = useState<Preview | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  async function execute(action: () => Promise<void>) {
    setBusy(true); setMessage('');
    try { await action(); } catch (error) { setMessage(errorMessage(error)); } finally { setBusy(false); }
  }
  function search() { void execute(async () => {
    const { data } = await apiClient.get('/collection-discovery/candidates', { params: { platformId, maxQueries: 3 }, timeout: 150000 });
    setCollections(data.candidates || []);
    setMessage(`${data.count || 0} collections found. Downloads are NOT enabled by this screen.`);
  }); }
  function stage() { void execute(async () => {
    const { data } = await apiClient.post('/collection-staging/zip', { archiveName: archive }, { timeout: 150000 });
    setFolder(data.collectionFolder);
    setPreview(null);
    setMessage(`${data.fileCount} files extracted to staging. Library unchanged.`);
  }); }
  function inspect() { void execute(async () => {
    const { data } = await apiClient.post<Preview>('/collection-import/preview', { collectionFolder: folder, platformId }, { timeout: 150000 });
    setPreview(data);
    setSelected(data.items.filter(i => i.defaultSelected && i.status !== 'ambiguous').map(i => i.file));
  }); }
  function importSelected() { void execute(async () => {
    if (!preview || !window.confirm(`Import ${selected.length} selected files into the library? Existing files will not be overwritten.`)) return;
    const { data } = await apiClient.post('/collection-import/import', {
      collectionFolder: folder, platformId, reviewToken: preview.reviewToken, selectedFiles: selected,
    }, { timeout: 300000 });
    const results: { status: string }[] = data.results || [];
    setMessage(`Import completed: ${results.filter(x => x.status === 'imported').length} imported; ${results.filter(x => x.status !== 'imported').length} skipped or flagged. Re-preview before further imports.`);
    setPreview(null);
  }); }
  return <div style={{ padding: 24, maxWidth: 1100 }}>
    <h1>Collection acquisition and review</h1>
    <p>This page uses RetroArr's authenticated API. Collection downloads and background scheduling are not enabled yet.</p>
    <label>Platform ID <input type="number" min={1} value={platformId} onChange={e => setPlatformId(Number(e.target.value))} /></label>
    <button disabled={busy || platformId <= 0} onClick={search}>Find collections</button>
    {collections.length > 0 && <table style={{ width: '100%' }}><thead><tr><th>Discovered collection</th><th>Size</th><th>Indexer</th></tr></thead><tbody>{collections.map((c,i) => <tr key={`${c.title}-${i}`}><td>{c.title}</td><td>{(c.size / 1073741824).toFixed(2)} GB</td><td>{c.indexerName}</td></tr>)}</tbody></table>}
    <hr />
    <h2>Stage downloaded ZIP</h2>
    <p>Place an archive directly in /downloads first. This only stages supported files for inspection.</p>
    <label>Archive filename <input value={archive} onChange={e => setArchive(e.target.value)} placeholder="Collection.zip" /></label>
    <button disabled={busy || !archive.trim()} onClick={stage}>Stage ZIP</button>
    <h2>Review staged files</h2>
    <label>Staging folder <input value={folder} onChange={e => { setFolder(e.target.value); setPreview(null); }} placeholder="retroarr-review-..." /></label>
    <button disabled={busy || !folder.trim()} onClick={inspect}>Match against catalogue</button>
    {preview && <><p>{preview.total} files: {preview.newGames} new, {preview.catalogued} catalogued, {preview.ambiguous} ambiguous.</p>
      <button disabled={busy} onClick={() => setSelected(preview.items.filter(i => i.defaultSelected && i.status !== 'ambiguous').map(i => i.file))}>Restore default selection</button>
      <button disabled={busy} onClick={() => setSelected([])}>Deselect all</button>
      <table style={{ width: '100%' }}><thead><tr><th>Import</th><th>Game</th><th>Status</th><th>Size</th></tr></thead><tbody>
        {preview.items.map(item => <tr key={item.file}><td><input type="checkbox" checked={selected.includes(item.file)} disabled={item.status === 'ambiguous'} onChange={e => setSelected(prev => e.target.checked ? [...prev, item.file] : prev.filter(f => f !== item.file))} /></td><td>{item.title}</td><td>{item.status}</td><td>{(item.size / 1048576).toFixed(1)} MB</td></tr>)}
      </tbody></table><button disabled={busy || selected.length === 0} onClick={importSelected}>Import {selected.length} selected files</button>
      <p>Ambiguous files cannot be imported here. Existing game files are not overwritten.</p></>}
    {message && <p role="status">{message}</p>}
  </div>;
}
