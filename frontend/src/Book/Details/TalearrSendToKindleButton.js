import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import { icons, messageTypes } from 'Helpers/Props';
import { showMessage } from 'Store/Actions/appActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';

// TaleArr: Send to Kindle on the book page. Hidden while Send to Kindle is off; disabled with the
// reason as its tooltip when the book has no file Amazon accepts. Uses /api/v1/talearr/kindle.
class TalearrSendToKindleButton extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      status: null,
      isSending: false
    };
  }

  componentDidMount() {
    this.fetchStatus();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId || prevProps.fileCount !== this.props.fileCount) {
      this.fetchStatus();
    }
  }

  fetchStatus() {
    const { request } = createAjaxRequest({ url: `/talearr/kindle/book/${this.props.bookId}` });

    request.done((status) => {
      this.setState({ status });
    });
  }

  onPress = () => {
    const { bookId, dispatch } = this.props;
    this.setState({ isSending: true });

    const { request } = createAjaxRequest({
      url: `/talearr/kindle/send/${bookId}`,
      method: 'POST',
      dataType: 'json'
    });

    request.done((result) => {
      this.setState({ isSending: false });
      dispatch(showMessage({
        id: `talearr-kindle-${bookId}`,
        name: 'TalearrSendToKindle',
        message: result.message,
        type: result.success ? messageTypes.SUCCESS : messageTypes.ERROR,
        hideAfter: result.success ? 6 : 12
      }));
    });

    request.fail(() => {
      this.setState({ isSending: false });
      dispatch(showMessage({
        id: `talearr-kindle-${bookId}`,
        name: 'TalearrSendToKindle',
        message: 'Send to Kindle failed. See System > Logs.',
        type: messageTypes.ERROR,
        hideAfter: 12
      }));
    });
  };

  render() {
    const { status, isSending } = this.state;

    if (!status || !status.enabled) {
      return null;
    }

    return (
      <PageToolbarButton
        label="Send to Kindle"
        title={status.message}
        iconName={icons.EXPORT}
        isSpinning={isSending}
        isDisabled={!status.canSend || isSending}
        onPress={this.onPress}
      />
    );
  }
}

TalearrSendToKindleButton.propTypes = {
  bookId: PropTypes.number.isRequired,
  fileCount: PropTypes.number,
  dispatch: PropTypes.func.isRequired
};

export default connect()(TalearrSendToKindleButton);
