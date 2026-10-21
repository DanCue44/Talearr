import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
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
import { describeRule, readError } from './seriesFormat';
import styles from './TalearrSeriesIndex.css';

const columns = [
  { name: 'title', label: 'Series', isVisible: true },
  { name: 'authors', label: 'Author', isVisible: true },
  { name: 'books', label: 'Books', isVisible: true },
  { name: 'audiobooks', label: 'Audiobooks', isVisible: true },
  { name: 'ebooks', label: 'Ebooks', isVisible: true },
  { name: 'monitoring', label: 'Monitoring', isVisible: true }
];

const storageKey = 'talearr.series.monitoredOnly';

function readMonitoredOnly() {
  try {
    return window.localStorage.getItem(storageKey) === 'true';
  } catch (e) {
    return false;
  }
}

// Talearr: every series in the library, with what Talearr monitors (docs/TALEARR_SERIES.md).
class TalearrSeriesIndex extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;

    this.state = {
      isFetching: true,
      error: null,
      items: [],
      monitoredOnly: readMonitoredOnly()
    };
  }

  componentDidMount() {
    this.fetchSeries();
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Data

  fetchSeries = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true });

    const { request, abortRequest } = createAjaxRequest({
      url: '/talearr/series'
    });

    this._abortRequest = abortRequest;

    request.done((items) => {
      this.setState({ isFetching: false, error: null, items });
    });

    request.fail((xhr) => {
      if (!xhr.aborted) {
        this.setState({ isFetching: false, error: readError(xhr, 'Unable to load series.') });
      }
    });
  };

  //
  // Listeners

  onRefreshPress = () => {
    this.fetchSeries();
  };

  onMonitoredOnlyChange = ({ value }) => {
    try {
      window.localStorage.setItem(storageKey, value ? 'true' : 'false');
    } catch (e) {
      // Not remembered; the toggle still works.
    }

    this.setState({ monitoredOnly: value });
  };

  //
  // Render

  render() {
    const { isFetching, error, items, monitoredOnly } = this.state;
    const shown = monitoredOnly ? items.filter((s) => !!s.rule) : items;

    return (
      <PageContent title="Series">
        <PageToolbar>
          <PageToolbarSection>
            {/* Talearr: opened from the Books toolbar, so offer the way back. */}
            <PageToolbarButton
              label="Books"
              iconName={icons.BOOK}
              to="/books"
            />

            <PageToolbarButton
              label="Refresh"
              iconName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={this.onRefreshPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <div className={styles.intro}>
            {'Monitor a series to have Talearr monitor and search every book in it, now and as new books appear, as audiobooks, ebooks or both.'}
          </div>

          <label className={styles.filter}>
            <CheckInput
              containerClassName={styles.check}
              name="monitoredOnly"
              value={monitoredOnly}
              onChange={this.onMonitoredOnlyChange}
            />
            {'Monitored series only'}
          </label>

          {
            isFetching && !items.length ?
              <LoadingIndicator /> :
              null
          }

          {
            error ?
              <Alert kind={kinds.DANGER}>{error}</Alert> :
              null
          }

          {
            !isFetching && !error && !shown.length ?
              <Alert kind={kinds.INFO}>
                {monitoredOnly ? 'No series are monitored yet. Open a series to monitor it.' : 'No series are in the library yet.'}
              </Alert> :
              null
          }

          {
            shown.length ?
              <Table columns={columns}>
                <TableBody>
                  {
                    shown.map((series) => {
                      return (
                        <TableRow key={series.seriesKey}>
                          <TableRowCell>
                            <Link to={`/talearr/series/${series.seriesId}`}>{series.title}</Link>
                          </TableRowCell>

                          <TableRowCell>{series.authors.join(', ')}</TableRowCell>
                          <TableRowCell>{series.bookCount}</TableRowCell>
                          <TableRowCell>{series.audiobookFiles} / {series.bookCount}</TableRowCell>
                          <TableRowCell>{series.ebookFiles} / {series.bookCount}</TableRowCell>

                          <TableRowCell>
                            <div className={styles.monitoring}>
                              <Label kind={series.rule ? kinds.PRIMARY : kinds.DISABLED}>
                                {describeRule(series.rule)}
                              </Label>
                              {
                                series.rule && series.rule.lastResult ?
                                  <span className={styles.lastResult}>{series.rule.lastResult}</span> :
                                  null
                              }
                            </div>
                          </TableRowCell>
                        </TableRow>
                      );
                    })
                  }
                </TableBody>
              </Table> :
              null
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

export default TalearrSeriesIndex;
