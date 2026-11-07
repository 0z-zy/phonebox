// UI tests of the panel and the settings page, against the web demo. They only use what a person sees and does
// (DOM, keys, clicks) plus the demo's PhoneBoxDemo helper, never the panel's internals.
const { test, expect } = require('@playwright/test');

/** Fails the test on any console error or uncaught exception. */
function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

const activeDot = (page) => page.locator('#dots i').evaluateAll((dots) => dots.findIndex((d) => d.classList.contains('on')));

test.beforeEach(async ({ page }) => {
  // every test starts from the demo's default layout
  await page.goto('/panel/');
  await page.evaluate(() => { try { localStorage.clear(); } catch (_) {} });
});

test('the panel starts without errors and shows its pages', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  expect(await page.locator('#dots i').count()).toBeGreaterThan(10);
  await page.waitForTimeout(1500); // a few state updates
  expect(errors).toEqual([]);
});

test('no widget fails to build on any page', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // a widget whose create() throws shows "<type>: <message>"
  const broken = await page.locator('.w > .empty').evaluateAll((els) => els.map((e) => e.textContent).filter((t) => /^[a-z]+: /.test(t)));
  expect(broken).toEqual([]);
});

test('keys flip pages and the overview jumps to a page', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  expect(await activeDot(page)).toBe(0);
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => activeDot(page)).toBe(1);
  await page.keyboard.press('ArrowLeft');
  await expect.poll(() => activeDot(page)).toBe(0);

  await page.keyboard.press('o');
  await expect(page.locator('#overview')).toBeVisible();
  await page.locator('.ov-tile').nth(3).click();
  await expect(page.locator('#overview')).toBeHidden();
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('a webhook notification shows a toast', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('notify', { title: 'Build #7', text: 'main passed', level: 'ok' }));
  await expect(page.locator('#toast')).toContainText('main passed');
});

test('a webhook can switch pages', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 3 }));
  await expect.poll(() => activeDot(page)).toBe(2);
});

test('a Claude Code permission prompt can be answered', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.ask());
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  await prompt.getByRole('button', { name: 'Allow' }).click();
  await expect(prompt).toBeHidden();
});

test('portrait arranges the widgets in six columns', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const cols = await page.locator('.grid').first().evaluate((g) => getComputedStyle(g).gridTemplateColumns.split(' ').length);
  expect(cols).toBe(6);
  await page.setViewportSize({ width: 780, height: 360 });
  await expect.poll(() => page.locator('.grid').first().evaluate((g) => getComputedStyle(g).gridTemplateColumns.split(' ').length)).toBe(12);
});

test('the settings page loads and its sections open', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab')).not.toHaveCount(0);
  for (const section of ['Shortcuts', 'Rules', 'Webhooks', 'General', 'Advanced', 'Pages']) {
    await page.locator('#nav a', { hasText: section }).click();
    await expect(page.locator('section.on h2')).toHaveText(section);
  }
  expect(errors).toEqual([]);
});

test('an edit in the settings page shows up on the panel right away', async ({ context }) => {
  const panel = await context.newPage();
  await panel.goto('/panel/');
  await expect(panel.locator('.w').first()).toBeVisible();

  const settings = await context.newPage();
  await settings.setViewportSize({ width: 1280, height: 860 });
  await settings.goto('/settings/#pages');
  await expect(settings.locator('#pageTabs .ptab')).not.toHaveCount(0);
  const title = settings.locator('#peHead input[placeholder="Page name"]');
  await title.fill('Edited in a test');
  await settings.locator('#save').click();

  // the overview lists the new name once the panel has the new settings (it is built when it opens)
  await expect.poll(async () => {
    await panel.keyboard.press('o');
    const text = await panel.locator('.ov-tile').first().textContent();
    await panel.keyboard.press('Escape');
    return text;
  }, { timeout: 10_000 }).toContain('Edited in a test');
});

test('the live preview in the layout editor runs the real panel', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab')).not.toHaveCount(0);
  await page.locator('#stageSeg button', { hasText: 'Live preview' }).click();
  const preview = page.frameLocator('#previewBox iframe');
  await expect(preview.locator('.w').first()).toBeVisible();
  // picking another page in the editor moves the preview there
  await page.locator('#pageTabs .ptab').nth(2).click();
  await expect.poll(() => preview.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')))).toBe(2);
});

test('rules can be added from the examples', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#rules');
  await page.locator('#rulePresets button').first().click();
  await expect(page.locator('#ruleList .rule')).toHaveCount(1);
  await expect(page.locator('#save')).toBeEnabled();
});

test('the landing page embeds the panel', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto('/');
  await expect(page.frameLocator('#panel').locator('.w').first()).toBeVisible();
  await page.getByRole('button', { name: /notification/ }).click();
  await expect(page.frameLocator('#panel').locator('#toast')).toBeVisible();
  expect(errors).toEqual([]);
});
