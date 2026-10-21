import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Alert from 'Components/Alert';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { icons, kinds } from 'Helpers/Props';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import fetchBookExplanation from './fetchBookExplanation';
import TalearrDecisionStep from './TalearrDecisionStep';
import styles from './TalearrBookExplain.css';

function createMapStateToProps() {
  return createSelector(
    createUISettingsSelector(),
    (uiSettings) => ({
      shortDateFormat: uiSettings.shortDateFormat,
      timeFormat: uiSettings.timeFormat
    })
  );
}

// Talearr: the book page "Why" tab. How the book was matched, then the recent searches and
// imports with their reasons, newest first (Chaptarr #81).
class TalearrBookExplain extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      explanation: null
    };
  }

  componentDidMount() {
    this._mounted = true;
    this.load();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId) {
      this.load();
    }
  }

  componentWillUnmount() {
    this._mounted = false;
  }

  //
  // Control

  load = (force = false) => {
    const { bookId } = this.props;

    this.setState({ isFetching: true, error: null });

    fetchBookExplanation(bookId, { force }).then(
      (explanation) => {
        if (this._mounted && bookId === this.props.bookId) {
          this.setState({ isFetching: false, explanation });
        }
      },
      () => {
        if (this._mounted) {
          this.setState({ isFetching: false, error: 'Could not load the explanation for this book.' });
        }
      }
    );
  };

  onRefreshPress = () => {
    this.load(true);
  };

  //
  // Render

  render() {
    const {
      shortDateFormat,
      timeFormat
    } = this.props;

    const {
      isFetching,
      error,
      explanation
    } = this.state;

    return (
      <div className={styles.container}>
        <div className={styles.header}>
          <div className={styles.intro}>
            {'Why Talearr matched this book, grabbed or skipped releases, and imported or refused files.'}
          </div>

          <SpinnerIconButton
            name={icons.REFRESH}
            title="Refresh"
            isSpinning={isFetching}
            onPress={this.onRefreshPress}
          />
        </div>

        {
          isFetching && !explanation ?
            <LoadingIndicator /> :
            null
        }

        {
          error ?
            <Alert kind={kinds.DANGER}>{error}</Alert> :
            null
        }

        {
          explanation ?
            <div>
              <div className={styles.sectionTitle}>{'Matching'}</div>
              <TalearrDecisionStep
                step={explanation.metadata}
                showTime={false}
              />

              <div className={styles.sectionTitle}>{'Searches and imports'}</div>
              {
                explanation.steps.length ?
                  explanation.steps.map((step, index) => (
                    <TalearrDecisionStep
                      key={`${step.createdAt}-${index}`}
                      step={step}
                      shortDateFormat={shortDateFormat}
                      timeFormat={timeFormat}
                    />
                  )) :
                  <div className={styles.empty}>
                    {'Nothing recorded yet. The next search or import for this book will show up here.'}
                  </div>
              }
            </div> :
            null
        }
      </div>
    );
  }
}

TalearrBookExplain.propTypes = {
  bookId: PropTypes.number.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired
};

export default connect(createMapStateToProps)(TalearrBookExplain);
