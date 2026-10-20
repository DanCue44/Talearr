import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { formatStatus, readError, skipText } from './seriesFormat';
import styles from './TalearrSeriesPage.css';

const columns = [
  { name: 'position', label: '#', isVisible: true },
  { name: 'title', label: 'Book', isVisible: true },
  { name: 'author', label: 'Author', isVisible: true },
  { name: 'audiobook', label: 'Audiobook', isVisible: true },
  { name: 'ebook', label: 'Ebook', isVisible: true }
];

function ruleToForm(rule) {
  return {
    audiobook: !!(rule && rule.audiobook),
    ebook: !!(rule && rule.ebook),
    searchOnAdd: rule ? rule.searchOnAdd : true,
    includeExtras: !!(rule && rule.includeExtras)
  };
}

function FormatCell({ state }) {
  const status = formatStatus(state);
  const label = (
    <Label kind={status.kind} title={status.title}>
      {status.label}
    </Label>
  );

  return (
    <TableRowCell>
      {
        state && state.bookId ?
          <Link to={`/book/${state.bookId}`}>{label}</Link> :
          label
      }
      {
        state && state.copies > 0 ?
          <span className={styles.copies} title="Additional recordings keep their own monitoring">
            +{state.copies} {state.copies === 1 ? 'recording' : 'recordings'}
          </span> :
          null
      }
    </TableRowCell>
  );
}

FormatCell.propTypes = {
  state: PropTypes.object
};

// Talearr: a series as a first-class page (docs/TALEARR_SERIES.md). Monitoring a series monitors
// its books in the chosen formats and keeps doing so for books that arrive later; the API is
// /api/v1/talearr/series.
class TalearrSeriesPage extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;

    this.state = {
      isFetching: true,
      error: null,
      detail: null,
      form: ruleToForm(null),
      isSaving: false,
      isApplying: false,
      isEnabling: null,
      saveError: null
    };
  }

  componentDidMount() {
    this.fetchDetail();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.match.params.seriesId !== this.props.match.params.seriesId) {
      this.fetchDetail();
    }
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Data

  getSeriesId() {
    return parseInt(this.props.match.params.seriesId);
  }

  setDetail = (detail) => {
    this.setState({
      isFetching: false,
      isSaving: false,
      isApplying: false,
      error: null,
      saveError: null,
      detail,
      form: ruleToForm(detail.series.rule)
    });
  };

  fetchDetail = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true });

    const { request, abortRequest } = createAjaxRequest({
      url: `/talearr/series/${this.getSeriesId()}`
    });

    this._abortRequest = abortRequest;

    request.done(this.setDetail);
    request.fail((xhr) => {
      if (xhr.aborted) {
        return;
      }

      this.setState({
        isFetching: false,
        error: xhr.status === 404 ? 'This series is not in the library.' : readError(xhr, 'Unable to load the series.')
      });
    });
  };

  //
  // Listeners

  onRefreshPress = () => {
    this.fetchDetail();
  };

  onFormChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: value } });
  };

  onSavePress = () => {
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: `/talearr/series/${this.getSeriesId()}`,
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(this.state.form)
    });

    request.done(this.setDetail);
    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save series monitoring.') });
    });
  };

  onApplyPress = () => {
    this.setState({ isApplying: true, saveError: null });

    const { request } = createAjaxRequest({
      url: `/talearr/series/${this.getSeriesId()}/apply`,
      method: 'POST',
      dataType: 'json'
    });

    request.done(this.setDetail);
    request.fail((xhr) => {
      this.setState({ isApplying: false, saveError: readError(xhr, 'Unable to apply series monitoring.') });
    });
  };

  onEnableAuthorsPress = (format) => {
    this.setState({ isEnabling: format, saveError: null });

    const { request } = createAjaxRequest({
      url: `/talearr/series/${this.getSeriesId()}/authors`,
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ format })
    });

    request.done((detail) => {
      this.setState({ isEnabling: null });
      this.setDetail(detail);
    });

    request.fail((xhr) => {
      this.setState({ isEnabling: null, saveError: readError(xhr, 'Unable to change the authors.') });
    });
  };

  //
  // Render

  renderPausedAuthors() {
    const { detail, isEnabling } = this.state;
    const rule = detail.series.rule;

    if (!rule) {
      return null;
    }

    return ['audiobook', 'ebook'].filter((format) => rule[format]).map((format) => {
      const paused = detail.entries.filter((entry) => {
        const state = entry[format];
        return !entry.skipReason && state && state.bookId && state.monitored && !state.hasFiles && !state.authorMonitored;
      });

      if (!paused.length) {
        return null;
      }

      const authors = [...new Set(paused.map((entry) => entry.authorName).filter((name) => !!name))];
      const noun = format === 'audiobook' ? 'audiobooks' : 'ebooks';

      return (
        <Alert
          key={format}
          kind={kinds.WARNING}
        >
          <div>
            {`${paused.length} monitored ${noun} of this series are not searched because ${authors.join(', ') || 'the author'} ${authors.length > 1 ? 'are' : 'is'} not monitored for ${noun}.`}
          </div>

          <div className={styles.actions}>
            <SpinnerButton
              kind={kinds.WARNING}
              isSpinning={isEnabling === format}
              onPress={() => this.onEnableAuthorsPress(format)}
            >
              {`Monitor ${noun} for ${authors.length > 1 ? 'these authors' : authors[0] || 'the author'}`}
            </SpinnerButton>

            <span className={styles.lastResult}>
              {'Only the author switch changes: no other book of the author is monitored.'}
            </span>
          </div>
        </Alert>
      );
    });
  }

  renderMonitoring() {
    const { detail, form, isSaving, isApplying, saveError } = this.state;
    const rule = detail.series.rule;
    const saved = ruleToForm(rule);
    const isChanged = Object.keys(form).some((key) => form[key] !== saved[key]);
    const isStopping = !!rule && !form.audiobook && !form.ebook;

    return (
      <div className={styles.monitoring}>
        <div className={styles.monitoringTitle}>
          {'Monitor this series'}
        </div>

        <div className={styles.options}>
          <label className={styles.option}>
            <CheckInput
              containerClassName={styles.check}
              name="audiobook"
              value={form.audiobook}
              onChange={this.onFormChange}
            />
            {'Audiobooks'}
          </label>

          <label className={styles.option}>
            <CheckInput
              containerClassName={styles.check}
              name="ebook"
              value={form.ebook}
              onChange={this.onFormChange}
            />
            {'Ebooks'}
          </label>

          <label className={styles.option}>
            <CheckInput
              containerClassName={styles.check}
              name="searchOnAdd"
              value={form.searchOnAdd}
              onChange={this.onFormChange}
            />
            {'Search when books are added'}
          </label>

          <label className={styles.option}>
            <CheckInput
              containerClassName={styles.check}
              name="includeExtras"
              value={form.includeExtras}
              onChange={this.onFormChange}
            />
            {'Include novellas and extras'}
          </label>
        </div>

        <div className={styles.help}>
          {'Every book of the series is monitored in the chosen formats, and so is each new book that appears in it later. A book you unmonitor afterwards stays unmonitored. Downloads follow your grab policy and recordings as usual; box sets are only included when the grab policy allows bundles.'}
          {isStopping ? ' Stopping leaves every book as it is now.' : ''}
        </div>

        {
          saveError ?
            <Alert kind={kinds.DANGER}>{saveError}</Alert> :
            null
        }

        <div className={styles.actions}>
          <SpinnerButton
            kind={kinds.PRIMARY}
            isSpinning={isSaving}
            isDisabled={!isChanged || isApplying}
            onPress={this.onSavePress}
          >
            {isStopping ? 'Stop monitoring' : 'Save'}
          </SpinnerButton>

          {
            rule ?
              <SpinnerButton
                isSpinning={isApplying}
                isDisabled={isSaving}
                onPress={this.onApplyPress}
              >
                {'Check for new books'}
              </SpinnerButton> :
              null
          }

          {
            rule && rule.lastResult ?
              <span className={styles.lastResult}>
                {`Last run: ${rule.lastResult}`}
              </span> :
              null
          }
        </div>
      </div>
    );
  }

  renderEntries() {
    const { detail } = this.state;

    if (!detail.entries.length) {
      return (
        <Alert kind={kinds.INFO}>
          {'No books of this series are in the library yet.'}
        </Alert>
      );
    }

    return (
      <Table columns={columns}>
        <TableBody>
          {
            detail.entries.map((entry) => {
              const skip = skipText(entry, detail.allowBundles);
              const link = (entry.ebook && entry.ebook.bookId) || (entry.audiobook && entry.audiobook.bookId);

              return (
                <TableRow
                  key={entry.baseBookId}
                  className={skip ? styles.skippedRow : undefined}
                >
                  <TableRowCell>
                    <span className={styles.position}>{entry.position || ''}</span>
                  </TableRowCell>

                  <TableRowCell>
                    <div className={styles.bookTitle}>
                      {link ? <Link to={`/book/${link}`}>{entry.title}</Link> : entry.title}
                      {skip ? <span className={styles.skip}>{skip}</span> : null}
                    </div>
                  </TableRowCell>

                  <TableRowCell>
                    {entry.authorName || ''}
                  </TableRowCell>

                  <FormatCell state={entry.audiobook} />
                  <FormatCell state={entry.ebook} />
                </TableRow>
              );
            })
          }
        </TableBody>
      </Table>
    );
  }

  render() {
    const { isFetching, error, detail } = this.state;
    const title = detail ? detail.series.title : 'Series';

    return (
      <PageContent title={title}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label="Refresh"
              iconName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={this.onRefreshPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          {
            isFetching && !detail ?
              <LoadingIndicator /> :
              null
          }

          {
            error ?
              <Alert kind={kinds.DANGER}>{error}</Alert> :
              null
          }

          {
            detail ?
              <div>
                <div className={styles.header}>
                  <h1 className={styles.title}>{detail.series.title}</h1>
                  {
                    detail.series.authors.length ?
                      <div className={styles.authors}>{detail.series.authors.join(', ')}</div> :
                      null
                  }
                  {
                    detail.description ?
                      <div className={styles.description}>{detail.description}</div> :
                      null
                  }
                </div>

                {this.renderMonitoring()}
                {this.renderPausedAuthors()}
                {this.renderEntries()}
              </div> :
              null
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

TalearrSeriesPage.propTypes = {
  match: PropTypes.shape({ params: PropTypes.shape({ seriesId: PropTypes.string.isRequired }).isRequired }).isRequired
};

export default TalearrSeriesPage;
