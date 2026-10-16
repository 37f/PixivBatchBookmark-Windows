"""Offline integration test: real Qt/Chromium against a local HTTP server."""

import json
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from PySide6.QtCore import QEventLoop, QTimer
from PySide6.QtWidgets import QApplication, QMessageBox
from pixiv.bookmark import Request
from pixiv.browser import BrowserSession
from ui.controller import BatchController
from ui.window import MainWindow


class FixtureHandler(BaseHTTPRequestHandler):
    private = None
    writes = []
    read_delay = 0

    def log_message(self, *_):
        pass

    def reply(self, value, code=200, content_type='application/json'):
        data = value.encode() if isinstance(value, str) else json.dumps(value).encode()
        self.send_response(code)
        self.send_header('Content-Type', content_type + '; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        try:
            self.wfile.write(data)
        except ConnectionError:
            pass

    def do_GET(self):
        if self.path == '/null':
            return self.reply(None)
        if self.path == '/limit':
            return self.reply({'error': True, 'message': 'limit'}, 429)
        if self.path == '/slow':
            time.sleep(2)
            return self.reply({'error': False, 'body': {}})
        if self.path.startswith('/ajax/illust/'):
            time.sleep(self.read_delay)
            bookmark = None if self.private is None else {'id': '55', 'private': self.private}
            return self.reply({'error': False, 'body': {'id': '97003966', 'title': 'Fixture', 'bookmarkData': bookmark}})
        if self.path.startswith('/bookmark_add.php'):
            if 'missing=comment' in self.path:
                return self.reply('<form class="bookmark-detail-unit"><input name="tag" value="fixture"></form>',
                                  content_type='text/html')
            return self.reply('<form class="bookmark-detail-unit"><input name="tag" value="fixture tag2">'
                              '<textarea name="comment">keep</textarea></form>', content_type='text/html')
        if self.path.startswith('/ajax/user/extra'):
            return self.reply({'error': False, 'body': {'following': 1}})
        state = {'api': {'token': 'fixture-csrf'}, 'userData': {'self': {'id': '12345', 'name': '离线测试账号'}}}
        data = {'props': {'pageProps': {'gaUserData': {'login': True},
                'serverSerializedPreloadedState': json.dumps(state)}}}
        return self.reply('<html><head><script id="__NEXT_DATA__" type="application/json">' +
                          json.dumps(data) + '</script></head><body>Offline fixture</body></html>', content_type='text/html')

    def do_POST(self):
        body = self.rfile.read(int(self.headers['Content-Length'])).decode()
        if self.headers.get('X-CSRF-TOKEN') != 'fixture-csrf':
            return self.reply({'error': True, 'message': 'csrf'}, 403)
        if self.path.endswith('/add'):
            payload = json.loads(body)
            type(self).private = bool(payload['restrict'])
        elif self.path.endswith('/delete'):
            payload = parse_qs(body)
            if payload != {'bookmark_id': ['55']}:
                return self.reply({'error': True, 'message': 'wrong bookmark id'}, 400)
            type(self).private = None
        else:
            return self.reply({'error': True}, 404)
        self.writes.append((self.path, payload))
        self.reply({'error': False, 'body': {}})


APP = QApplication.instance() or QApplication([])
APP.setApplicationName('PixivBatchBookmark-QA')


def wait_until(predicate, milliseconds=15000):
    limit = time.monotonic() + milliseconds / 1000
    while not predicate() and time.monotonic() < limit:
        loop = QEventLoop()
        QTimer.singleShot(20, loop.quit)
        loop.exec()
    if not predicate():
        raise AssertionError('Qt event deadline exceeded')


class QtIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.server = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.session = BrowserSession(Path(cls.temp.name), base_url=f'http://127.0.0.1:{cls.server.server_port}')
        cls.session.load_home()
        wait_until(lambda: cls.session.ready)
        cls.session.check_login()
        wait_until(lambda: cls.session.logged_in)

    @classmethod
    def tearDownClass(cls):
        cls.session.shutdown()
        APP.processEvents()
        cls.server.shutdown()
        # Chromium may hold cache files until process exit; do not recursively
        # remove the active profile here.
        cls.temp._finalizer.detach()

    def fetch(self, request):
        result = []
        self.session.request(request, lambda response, error: result.append((response, error)))
        wait_until(lambda: bool(result))
        return result[0]

    def test_browser_login_reads_next_data_and_authenticated_api(self):
        self.assertTrue(self.session.logged_in)
        self.assertEqual(self.session.user_id, '12345')

    def test_login_refresh_invalidates_old_ready_state_immediately(self):
        self.session.check_login()
        self.assertFalse(self.session.logged_in)
        wait_until(lambda: self.session.logged_in)

    def test_actual_browser_add_and_delete(self):
        response, error = self.fetch(Request('POST', '/ajax/illusts/bookmarks/add',
            {'illust_id': '97003966', 'restrict': 1, 'tags': [], 'comment': ''}))
        self.assertIsNone(error)
        response, error = self.fetch(Request('GET', '/ajax/illust/97003966'))
        self.assertTrue(response['body']['bookmarkData']['private'])
        response, error = self.fetch(Request('POST', '/ajax/illusts/bookmarks/delete', {'bookmark_id': '55'}, 'form'))
        self.assertIsNone(error)
        self.assertIsNone(FixtureHandler.private)

    def test_metadata_read_and_http_error(self):
        response, error = self.fetch(Request('GET', '/bookmark_add.php?type=illust&illust_id=97003966',
                                           response_type='bookmark_metadata'))
        self.assertIsNone(error)
        self.assertEqual(response['body'], {'tags': ['fixture', 'tag2'], 'comment': 'keep'})
        response, error = self.fetch(Request('GET', '/limit'))
        self.assertEqual(error.status, 429)
        self.assertTrue(error.stop_batch)

    def test_missing_comment_field_does_not_synthesize_empty_metadata(self):
        response, error = self.fetch(Request('GET', '/bookmark_add.php?type=illust&illust_id=97003966&missing=comment',
                                           response_type='bookmark_metadata'))
        self.assertIsNone(response)
        self.assertIn('备注', str(error))

    def test_null_json_schema_is_fatal(self):
        response, error = self.fetch(Request('GET', '/null'))
        self.assertTrue(error.stop_batch)

    def test_resume_with_queued_next_item_does_not_start_duplicate_work(self):
        FixtureHandler.private = None
        controller = BatchController(self.session)
        controller.interval_ms = 5
        controller.start(['97003966', '97003966'], 'bookmark', False)
        # Queue a second boundary callback as can happen when Resume runs before
        # the completion's singleShot callback. Exactly one flow may be active.
        FixtureHandler.read_delay = 0.1
        QTimer.singleShot(15, controller._next_item)
        try:
            wait_until(lambda: not controller.running)
            self.assertEqual(len(controller.results), 2)
            self.assertEqual([r.status for r in controller.results], ['success', 'skipped'])
        finally:
            FixtureHandler.read_delay = 0

    def test_browser_timeout_does_not_hang(self):
        self.session.timeout_ms = 150
        try:
            response, error = self.fetch(Request('GET', '/slow'))
            self.assertIn('超时', str(error))
        finally:
            self.session.timeout_ms = 25000

    def test_ui_parser_batch_and_render(self):
        FixtureHandler.private = None
        window = MainWindow(session=self.session)
        window.show()
        window.input_edit.setPlainText('pixiv id：97003966\n97003966')
        window.parse_input()
        self.assertEqual(window.ids, ['97003966'])
        results = []
        window.controller.item_done.connect(results.append)
        window.controller.interval_ms = 5
        window.controller.start(window.ids, 'bookmark', False)
        wait_until(lambda: not window.controller.running)
        self.assertEqual(results[0].status, 'success')
        self.assertFalse(FixtureHandler.private)
        self.assertEqual(window.table.rowCount(), 1)
        path = Path(self.temp.name) / 'ui-preview.png'
        self.assertTrue(window.grab().save(str(path)))
        window.close()

    def test_quick_buttons_parse_input_and_use_explicit_mode_without_confirmation(self):
        for name, original, wanted in (
                ('quick_public_button', True, False),
                ('quick_private_button', None, True)):
            with self.subTest(button=name):
                FixtureHandler.private = original
                FixtureHandler.writes.clear()
                window = MainWindow(session=self.session)
                try:
                    quick = getattr(window, name, None)
                    self.assertIsNotNone(quick, 'Missing quick bookmark button')
                    window.input_edit.setPlainText('pixiv id：97003966\n97003966')
                    (window.public_radio if wanted else window.private_radio).setChecked(True)
                    window.controller.interval_ms = 5
                    with patch('ui.window.QMessageBox.question', side_effect=AssertionError('Unexpected confirmation')):
                        quick.click()
                        self.assertTrue(window.controller.running)
                        self.assertFalse(window.quick_public_button.isEnabled())
                        self.assertFalse(window.quick_private_button.isEnabled())
                        # A queued/programmatic activation must not reset the active batch.
                        quick.click()
                        window.start_batch('bookmark')
                        wait_until(lambda: not window.controller.running)
                    self.assertEqual(window.ids, ['97003966'])
                    self.assertEqual(len(window.controller.results), 1)
                    self.assertEqual(window.controller.results[0].status, 'success')
                    self.assertEqual(FixtureHandler.private, wanted)
                    adds = [payload for path, payload in FixtureHandler.writes if path.endswith('/add')]
                    self.assertEqual(len(adds), 1)
                    self.assertEqual(adds[0]['restrict'], int(wanted))
                    if original is not None:
                        self.assertEqual(adds[0]['tags'], ['fixture', 'tag2'])
                        self.assertEqual(adds[0]['comment'], 'keep')
                    self.assertEqual(window.private_radio.isChecked(), wanted)
                    self.assertTrue(window.quick_public_button.isEnabled())
                    self.assertTrue(window.quick_private_button.isEnabled())
                finally:
                    window.close()

    def test_quick_buttons_keep_login_empty_input_and_version_guards(self):
        window = MainWindow(session=self.session)
        try:
            quick = getattr(window, 'quick_private_button', None)
            self.assertIsNotNone(quick, 'Missing quick bookmark button')
            FixtureHandler.writes.clear()
            with patch('ui.window.QMessageBox.information'), patch.object(window, 'show_login') as login:
                quick.click()
                self.assertFalse(window.controller.running)
                window.input_edit.setPlainText('97003966')
                self.session.logged_in = False
                quick.click()
                self.assertFalse(window.controller.running)
                login.assert_called_once()
            self.session.logged_in = True
            window.version_switcher._pending = True
            quick.click()
            self.assertFalse(window.controller.running)
            window.version_switcher._pending = False
            window.version_switcher.shutdown_blocked = True
            window._refresh_version_button()
            self.assertFalse(quick.isEnabled())
            self.assertFalse(window.quick_public_button.isEnabled())
            self.assertFalse(window.controller.running)
            self.assertEqual(FixtureHandler.writes, [])
        finally:
            self.session.logged_in = True
            window.version_switcher._pending = False
            window.version_switcher.shutdown_blocked = False
            window.close()

    def test_regular_bookmark_still_confirms_and_cancel_preserves_results(self):
        window = MainWindow(session=self.session)
        try:
            window.input_edit.setPlainText('97003966')
            window.table.setRowCount(1)
            FixtureHandler.writes.clear()
            with patch('ui.window.QMessageBox.question', return_value=QMessageBox.StandardButton.No) as confirm:
                window.bookmark_button.click()
            confirm.assert_called_once()
            self.assertFalse(window.controller.running)
            self.assertEqual(window.table.rowCount(), 1)
            self.assertEqual(FixtureHandler.writes, [])
        finally:
            window.close()

    def test_quick_action_layout_does_not_overlap_at_minimum_window_size(self):
        window = MainWindow(session=self.session)
        window.show()
        try:
            for width, height in ((1160, 820), (960, 720)):
                window.resize(width, height)
                APP.processEvents()
                card = window.quick_public_button.parentWidget()
                controls = [window.quick_public_button, window.quick_private_button,
                            window.bookmark_button, window.remove_button]
                rectangles = []
                for control in controls:
                    rect = control.rect().translated(control.mapTo(card, control.rect().topLeft()))
                    self.assertTrue(card.rect().contains(rect), (width, height, control.text()))
                    for other in rectangles:
                        self.assertFalse(rect.intersects(other), (width, height, control.text()))
                    rectangles.append(rect)
                note = card.layout().itemAt(card.layout().count() - 1).widget()
                self.assertGreaterEqual(note.height(), note.heightForWidth(note.width()))
        finally:
            window.close()

    def test_stop_finishes_current_conversion_before_ending(self):
        FixtureHandler.private = False
        controller = BatchController(self.session)
        controller.interval_ms = 5
        controller.start(['97003966', '97003966'], 'bookmark', True)
        # Stop after the first read has been sent, before the conversion writes.
        wait_until(lambda: controller._flow is not None)
        controller.stop()
        wait_until(lambda: not controller.running)
        self.assertEqual(len(controller.results), 1)
        self.assertEqual(controller.results[0].status, 'success')
        self.assertTrue(FixtureHandler.private)

    def test_pause_does_not_interrupt_current_work_and_resumes_remaining(self):
        FixtureHandler.private = None
        controller = BatchController(self.session)
        controller.interval_ms = 5
        controller.start(['97003966', '97003966'], 'bookmark', False)
        wait_until(lambda: controller._flow is not None)
        controller.toggle_pause()
        wait_until(lambda: len(controller.results) == 1 and controller._flow is None)
        self.assertTrue(controller.running)
        self.assertTrue(controller.paused)
        controller.toggle_pause()
        wait_until(lambda: not controller.running)
        self.assertEqual(len(controller.results), 2)
        self.assertEqual([r.status for r in controller.results], ['success', 'skipped'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
